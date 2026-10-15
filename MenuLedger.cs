using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Suran.QQBridge;

/// <summary>
/// 编号菜单台账：把发出去的「1.xxx 2.xxx」选项登记成活动，用户回一条文本就能对上是哪个活动的第几项，
/// 并按 once/max/per/ttl/until/cooldown 判定这次选择还有没有效。QQ 个人号侧没有按钮回调，这份台账就是点按钮的替代通道。
/// </summary>
public sealed class MenuLedger(int capacity)
{
    public sealed record MatchResult(MenuRecord Menu, int OptionIndex, bool Allowed, string Verdict);

    public sealed class OptionState
    {
        public int Total { get; set; }
        public Dictionary<long, int> UserCounts { get; set; } = [];
        public Dictionary<long, DateTime> LastClicks { get; set; } = [];
    }

    public sealed class MenuRecord
    {
        public int Seq { get; set; }
        public string Code { get; set; } = "";
        public string Title { get; set; } = "";
        public long GroupId { get; set; }
        public long OwnerUserId { get; set; }
        public long? MessageId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public int MaxTotal { get; set; }
        public int PerUser { get; set; }
        public int CooldownSeconds { get; set; }
        public bool Hard { get; set; } = true;
        public bool Shared { get; set; }
        public bool Notify { get; set; }
        public List<string> Options { get; set; } = [];
        public List<OptionState> States { get; set; } = [];
        public int Total { get; set; }
        public int CooldownHits { get; set; }
        public bool Closed { get; set; }
        public string ClosedReason { get; set; } = "";
        public DateTime? ClosedAt { get; set; }

        public DateTime? Deadline => ExpiresAt;
        public int OptionCount => Options.Count;

        public string OptionLabel(int optionIndex)
        {
            return optionIndex >= 0 && optionIndex < Options.Count ? Options[optionIndex] : "";
        }

        public OptionState StateOf(int optionIndex)
        {
            int target = Shared ? 0 : optionIndex;
            while (States.Count <= target)
            {
                States.Add(new OptionState());
            }
            return States[target];
        }
    }

    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public int Sequence { get; set; }
        public List<MenuRecord> Records { get; set; } = [];
    }

    const int MaxCandidateRows = 10;
    const int TitleMaxLength = 40;
    const int ReplyMaxLength = 60;

    static readonly JsonSerializerOptions SnapshotStyle = new() { WriteIndented = true };

    readonly Dictionary<string, MenuRecord> records = [];

    int sequence;

    public int Count => records.Count;

    public MenuRecord Create(long groupId, long ownerUserId, string title, List<string> options,
        int ttlSeconds, string? until, int maxTotal, int perUser, bool once, int cooldownSeconds,
        bool hard, bool shared, bool notify)
    {
        sequence++;
        MenuRecord record = new()
        {
            Seq = sequence,
            Code = "~" + ToBase36(sequence),
            Title = title.Length > TitleMaxLength ? title[..TitleMaxLength] : title,
            GroupId = groupId,
            OwnerUserId = ownerUserId,
            CreatedAt = DateTime.Now,
            ExpiresAt = ResolveDeadline(ttlSeconds, until),
            MaxTotal = once ? 1 : Math.Max(maxTotal, 0),
            PerUser = once ? 1 : Math.Max(perUser, 0),
            CooldownSeconds = Math.Max(cooldownSeconds, 0),
            Hard = hard,
            Shared = shared,
            Notify = notify,
            Options = [.. options],
        };
        if (record.Shared)
        {
            record.StateOf(0);
        }

        records[Key(record.Seq)] = record;
        Trim();
        return record;
    }

    public MatchResult? TryMatch(long userId, long groupId, string replyText)
    {
        ParsedReply? parsed = ParseReply(replyText);
        if (parsed == null)
        {
            return null;
        }

        List<Candidate> candidates = CollectCandidates(userId, groupId, parsed);
        if (candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count > 1)
        {
            return new MatchResult(candidates[0].Menu, candidates[0].OptionIndex, false,
                "有 " + candidates.Count + " 个菜单都能对上这个说法，别猜："
                + string.Join("、", candidates.ConvertAll(candidate => FormatShort(candidate.Menu)).ToArray())
                + "；要么等对方说清是哪个菜单，要么把菜单编号一起报出来");
        }

        Candidate candidate = candidates[0];
        MenuRecord menu = candidate.Menu;
        int optionIndex = candidate.OptionIndex;
        if (optionIndex < 0)
        {
            return new MatchResult(menu, 0, false, parsed.RawIndex.Length > 0
                ? "菜单「" + menu.Title + "」只有 " + menu.Options.Count + " 项，没有第 " + parsed.RawIndex + " 项"
                : "是对菜单「" + menu.Title + "」的回应，但没说选第几项（这项菜单有 " + menu.Options.Count
                  + " 项，编号 1-" + menu.Options.Count + "），要执行得先让对方补个编号");
        }
        if (menu.Closed)
        {
            return new MatchResult(menu, optionIndex, false,
                "菜单「" + menu.Title + "」已经" + CloseText(menu.ClosedReason) + Guard(menu));
        }
        if (menu.ExpiresAt != null && DateTime.Now > menu.ExpiresAt)
        {
            Close(menu, "expired");
            return new MatchResult(menu, optionIndex, false,
                "菜单「" + menu.Title + "」已过期（截止 " + menu.ExpiresAt!.Value.ToString("MM-dd HH:mm") + "）" + Guard(menu));
        }

        string label = menu.Options[optionIndex];
        OptionState state = menu.StateOf(optionIndex);
        int userCount = state.UserCounts.TryGetValue(userId, out int known) ? known : 0;

        if (menu.CooldownSeconds > 0 && state.LastClicks.TryGetValue(userId, out DateTime lastClick))
        {
            int elapsed = (int)(DateTime.Now - lastClick).TotalSeconds;
            if (elapsed < menu.CooldownSeconds)
            {
                menu.CooldownHits++;
                return new MatchResult(menu, optionIndex, false,
                    "菜单「" + menu.Title + "」第 " + (optionIndex + 1) + " 项在冷却中，还要等 "
                    + (menu.CooldownSeconds - elapsed) + " 秒" + Guard(menu));
            }
        }

        if (menu.MaxTotal > 0 && menu.Total >= menu.MaxTotal)
        {
            Close(menu, "full");
            return new MatchResult(menu, optionIndex, false,
                "菜单「" + menu.Title + "」名额已满（共 " + menu.MaxTotal + " 份）" + Guard(menu));
        }
        if (menu.PerUser > 0 && userCount >= menu.PerUser)
        {
            return new MatchResult(menu, optionIndex, false,
                "这个人已经选过菜单「" + menu.Title + "」第 " + (optionIndex + 1) + " 项 " + userCount
                + " 次，每人上限 " + menu.PerUser + " 次" + Guard(menu));
        }

        state.Total++;
        state.UserCounts[userId] = userCount + 1;
        state.LastClicks[userId] = DateTime.Now;
        menu.Total++;

        string verdict = "是对菜单「" + menu.Title + "」第 " + (optionIndex + 1) + " 项「" + label + "」的选择";
        verdict += menu.MaxTotal > 0 ? "｜名额 " + menu.Total + "/" + menu.MaxTotal : "｜第 " + menu.Total + " 次";
        verdict += "，有效";
        if (menu.MaxTotal > 0 && menu.Total >= menu.MaxTotal)
        {
            Close(menu, "full");
            verdict += menu.Hard ? "；刚好收满，后面的选择都作废了" : "；名额到这里用完了";
        }
        else if (menu.Notify && menu.PerUser > 0 && userCount + 1 >= menu.PerUser)
        {
            verdict += "；这个人的可选次数已经用满";
        }
        return new MatchResult(menu, optionIndex, true, verdict);
    }

    public MenuRecord? Find(string selector)
    {
        string wanted = (selector ?? "").Trim();
        if (wanted.Length == 0)
        {
            return null;
        }
        if (wanted.StartsWith('#'))
        {
            wanted = wanted[1..];
        }
        foreach (MenuRecord record in records.Values)
        {
            bool codeHit = record.Code == (wanted.StartsWith('~') ? wanted : "~" + wanted);
            if (record.Seq.ToString() == wanted || record.Title == wanted || codeHit)
            {
                return record;
            }
        }
        return null;
    }

    public bool Close(string selector, string reason)
    {
        MenuRecord? menu = Find(selector);
        if (menu == null || menu.Closed)
        {
            return false;
        }
        Close(menu, reason);
        return true;
    }

    static void Close(MenuRecord record, string reason)
    {
        record.Closed = true;
        record.ClosedReason = reason;
        record.ClosedAt = DateTime.Now;
    }

    public bool Reset(string selector)
    {
        MenuRecord? menu = Find(selector);
        if (menu == null)
        {
            return false;
        }
        menu.Total = 0;
        menu.States.Clear();
        menu.CooldownHits = 0;
        menu.Closed = false;
        menu.ClosedReason = "";
        menu.ClosedAt = null;
        if (menu.Shared)
        {
            menu.StateOf(0);
        }
        return true;
    }

    public bool Extend(string selector, int minutes)
    {
        MenuRecord? menu = Find(selector);
        if (menu == null || minutes < 1)
        {
            return false;
        }
        DateTime baseTime = menu.ExpiresAt == null || menu.ExpiresAt < DateTime.Now ? DateTime.Now : menu.ExpiresAt!.Value;
        menu.ExpiresAt = baseTime.AddMinutes(minutes);
        if (menu.ClosedReason == "expired")
        {
            menu.Closed = false;
            menu.ClosedReason = "";
            menu.ClosedAt = null;
        }
        return true;
    }

    public string FormatStats(string? filter, int limit)
    {
        List<MenuRecord> listed = [];
        foreach (MenuRecord record in records.Values)
        {
            if (MatchesFilter(record, filter))
            {
                listed.Add(record);
            }
        }
        if (listed.Count == 0)
        {
            return string.IsNullOrEmpty(filter)
                ? "台账是空的，还没发过编号菜单。"
                : "台账里没有匹配「" + filter + "」的菜单，可以用标题词、#序号 或菜单短码来筛。";
        }

        listed.Sort((left, right) => right.Seq.CompareTo(left.Seq));
        List<string> lines = new()
        {
            "菜单台账共 " + records.Count + " 个活动，显示 " + Math.Min(limit, listed.Count) + " 个（按新旧排）：",
        };
        for (int index = 0; index < Math.Min(limit, listed.Count); index++)
        {
            lines.Add(FormatRow(listed[index]));
        }
        return string.Join("\n", lines);
    }

    public static string FormatRow(MenuRecord record)
    {
        string state = record.Closed
            ? CloseText(record.ClosedReason)
            : record.ExpiresAt == null ? "开放中" : "开放中，截止 " + record.ExpiresAt!.Value.ToString("MM-dd HH:mm");
        string quota = record.MaxTotal > 0 ? record.Total + "/" + record.MaxTotal : record.Total + " 次";
        string line = "#" + record.Seq + " " + record.Code + "「" + record.Title + "」" + record.Options.Count
            + " 项｜已选 " + quota + "｜" + state;
        if (record.GroupId != 0)
        {
            line += "｜群 " + record.GroupId;
        }
        if (record.Hard == false)
        {
            line += "｜超限仍转告";
        }
        if (record.CooldownHits > 0)
        {
            line += "｜冷却拦截 " + record.CooldownHits + " 次";
        }
        return line;
    }

    static bool MatchesFilter(MenuRecord record, string? filter)
    {
        string needle = (filter ?? "").Trim();
        if (needle.Length == 0)
        {
            return true;
        }
        return record.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || record.Code.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || record.Seq.ToString() == needle.TrimStart('#');
    }

    // hard=true 直接把话说明白；hard=false 只报事实，拦不拦由模型自己判断
    static string Guard(MenuRecord menu)
        => menu.Hard ? "，这条选择不要执行" : "，这条没算进名额，要不要照办你自己拿主意";

    static string CloseText(string reason) => reason switch
    {
        "full" => "收满",
        "expired" => "过期",
        "manual" => "被手动关闭",
        _ => "关闭",
    };

    static string FormatShort(MenuRecord record) => "#" + record.Seq + "「" + record.Title + "」";

    List<Candidate> CollectCandidates(long userId, long groupId, ParsedReply parsed)
    {
        List<Candidate> matched = [];
        List<Candidate> overflowed = [];
        foreach (MenuRecord record in records.Values)
        {
            // 会话位置必须对上：群菜单只在同一个群里认，私聊菜单只对发起人本人认
            bool samePlace = record.GroupId == groupId
                && (record.GroupId != 0 || record.OwnerUserId == userId);
            if (samePlace == false)
            {
                continue;
            }
            if (parsed.Code.Length > 0 && record.Code != parsed.Code)
            {
                continue;
            }

            if (parsed.OptionIndex >= 0)
            {
                // 报了编号：对得上就是这项，超出范围也要留着，让调用方说清「这个菜单没这一项」
                if (parsed.OptionIndex < record.Options.Count)
                {
                    matched.Add(new Candidate(record, parsed.OptionIndex, record.OwnerUserId == userId));
                }
                else
                {
                    overflowed.Add(new Candidate(record, -1, record.OwnerUserId == userId));
                }
                continue;
            }

            int labelIndex = LabelIndex(record, parsed.Text);
            if (labelIndex >= 0)
            {
                matched.Add(new Candidate(record, labelIndex, record.OwnerUserId == userId));
            }
            else if (parsed.Code.Length > 0)
            {
                // 只报了菜单短码，没报第几项：菜单能认出来，选项还得对方补
                overflowed.Add(new Candidate(record, -1, record.OwnerUserId == userId));
            }
        }

        List<Candidate> pool = matched.Count > 0 ? matched : overflowed;
        if (pool.Count == 0)
        {
            return pool;
        }
        pool.Sort(RankCandidates);

        bool topIsMine = pool[0].IsMine;
        List<Candidate> top = [];
        foreach (Candidate candidate in pool)
        {
            if (candidate.IsMine != topIsMine)
            {
                break;
            }
            top.Add(candidate);
        }
        if (parsed.Code.Length > 0 || top.Count == 1)
        {
            return [top[0]];
        }
        if (top.Count > MaxCandidateRows)
        {
            top.RemoveRange(MaxCandidateRows, top.Count - MaxCandidateRows);
        }
        return top;
    }

    static int RankCandidates(Candidate left, Candidate right)
    {
        if (left.IsMine != right.IsMine)
        {
            return left.IsMine ? -1 : 1;
        }
        return right.Menu.Seq.CompareTo(left.Menu.Seq);
    }

    static int LabelIndex(MenuRecord record, string text)
    {
        for (int index = 0; index < record.Options.Count; index++)
        {
            if (string.Equals(record.Options[index].Trim(), text, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    static ParsedReply? ParseReply(string text)
    {
        string body = text.Trim();
        if (body.Length == 0 || body.Length > ReplyMaxLength)
        {
            return null;
        }

        string code = "";
        int codeStart = body.IndexOf('~');
        if (codeStart >= 0)
        {
            int codeEnd = codeStart + 1;
            while (codeEnd < body.Length && (char.IsLetterOrDigit(body[codeEnd]) || body[codeEnd] == '_'))
            {
                codeEnd++;
            }
            if (codeEnd - codeStart < 2)
            {
                return null;
            }
            code = body[codeStart..codeEnd];
            body = body.Remove(codeStart, codeEnd - codeStart).Trim();
        }

        string head = "";
        foreach (char character in body)
        {
            if (char.IsAsciiDigit(character) == false)
            {
                break;
            }
            head += character;
        }
        int optionIndex = head.Length > 0 && int.TryParse(head, out int number) ? number - 1 : -1;
        if (optionIndex < 0)
        {
            optionIndex = IndexAfterOrderWord(body);
        }
        if (code.Length == 0 && optionIndex < 0 && body.Length == 0)
        {
            return null;
        }
        return new ParsedReply(code, optionIndex, head, body);
    }

    static int IndexAfterOrderWord(string text)
    {
        foreach (string word in new[] { "第", "选", "要", "#" })
        {
            int position = text.IndexOf(word, StringComparison.Ordinal);
            if (position < 0)
            {
                continue;
            }
            string tail = text[(position + word.Length)..].TrimStart();
            string digits = "";
            foreach (char character in tail)
            {
                if (char.IsAsciiDigit(character) == false)
                {
                    break;
                }
                digits += character;
            }
            if (digits.Length > 0 && int.TryParse(digits, out int value))
            {
                return value - 1;
            }
        }
        return -1;
    }

    static string Key(int seq) => seq.ToString(CultureInfo.InvariantCulture);

    static string ToBase36(int value)
    {
        if (value == 0)
        {
            return "0";
        }
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Stack<char> buffer = new();
        int remaining = value;
        while (remaining > 0)
        {
            buffer.Push(digits[remaining % 36]);
            remaining /= 36;
        }
        return new string(buffer.ToArray());
    }

    static DateTime? ResolveDeadline(int ttlSeconds, string? until)
    {
        if (ttlSeconds > 0)
        {
            return DateTime.Now.AddSeconds(ttlSeconds);
        }
        return ParseUntil(until);
    }

    static DateTime? ParseUntil(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Contains(':'))
        {
            string[] pieces = value.Split(':');
            if ((pieces.Length == 2 || pieces.Length == 3)
                && int.TryParse(pieces[0], out int hour) && int.TryParse(pieces[1], out int minute)
                && hour >= 0 && hour < 24 && minute >= 0 && minute < 60)
            {
                int second = pieces.Length == 3 && int.TryParse(pieces[2], out int parsedSecond) ? parsedSecond : 0;
                DateTime clockTime = DateTime.Today.AddHours(hour).AddMinutes(minute).AddSeconds(second);
                return clockTime <= DateTime.Now ? clockTime.AddDays(1) : clockTime;
            }
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double unix) && unix > 1_000_000_000)
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)unix).LocalDateTime;
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime absolute))
        {
            return absolute;
        }
        return null;
    }

    void Trim()
    {
        if (records.Count <= capacity)
        {
            return;
        }
        MenuRecord? victim = null;
        foreach (MenuRecord record in records.Values)
        {
            if (record.Closed == false)
            {
                continue;
            }
            if (victim == null || record.Seq < victim.Seq)
            {
                victim = record;
            }
        }
        if (victim == null)
        {
            foreach (MenuRecord record in records.Values)
            {
                if (victim == null || record.Seq < victim.Seq)
                {
                    victim = record;
                }
            }
        }
        if (victim != null)
        {
            records.Remove(Key(victim.Seq));
        }
    }

    public void Save(string path)
    {
        Snapshot snapshot = new() { Sequence = sequence };
        foreach (MenuRecord record in records.Values)
        {
            snapshot.Records.Add(record);
        }

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) == false)
        {
            Directory.CreateDirectory(directory!);
        }
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, SnapshotStyle));
        File.Move(tempPath, path, true);
    }

    public void Load(string path)
    {
        if (File.Exists(path) == false)
        {
            return;
        }
        Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
        if (snapshot == null)
        {
            return;
        }

        foreach (MenuRecord record in snapshot.Records)
        {
            records[Key(record.Seq)] = record;
        }
        sequence = Math.Max(snapshot.Sequence, LatestSeq());
        Trim();
    }

    int LatestSeq()
    {
        int latest = 0;
        foreach (MenuRecord record in records.Values)
        {
            latest = Math.Max(latest, record.Seq);
        }
        return latest;
    }

    sealed record Candidate(MenuRecord Menu, int OptionIndex, bool IsMine);

    sealed record ParsedReply(string Code, int OptionIndex, string RawIndex, string Text);
}
