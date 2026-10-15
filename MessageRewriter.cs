using System;
using System.Text.RegularExpressions;

namespace Suran.QQBridge;

/// <summary>
/// 改写进入对话的 QQ 消息文本：把裸QQ号的@换成名字、把框架不认识的原始消息段压成一行、
/// 标注机器人自己的回声，并把回复对回本插件发出的编号菜单。
/// </summary>
public sealed class MessageRewriter(PersonDirectory directory)
{
    public bool ResolveAtName { get; set; } = true;
    public bool PreferGroupCard { get; set; } = true;
    public bool NormalizeSegments { get; set; } = true;
    public bool MarkSelfEcho { get; set; } = true;
    public bool BindMenuReplies { get; set; } = true;
    public long BotId { get; set; }
    public MenuLedger? Ledger { get; set; }

    static readonly Regex SpeakerPattern = new(
        @"^(?<private>\[私聊\])?\[(?<id>\d{4,12})(?:\((?<name>[^)]*)\))?\]:(?<body>.*)$",
        RegexOptions.Compiled);

    static readonly Regex GroupHeaderPattern = new(
        @"\[Q群消息\((?<id>\d+)(?:,(?<name>[^)\]]*))?\)\]",
        RegexOptions.Compiled);

    static readonly Regex AtPattern = new(@"@(?<id>\d{4,12})(?![0-9])", RegexOptions.Compiled);

    static readonly Regex RecordPattern = new(@"\[CQ:record[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex VideoPattern = new(@"\[CQ:video[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex CardPattern = new(@"\[CQ:(?:json|xml)[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex MusicPattern = new(@"\[CQ:music[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex LocationPattern = new(@"\[CQ:pos[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex LinkPattern = new(@"(https?://[^\s,\]\[\{}""'<>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex CardTitlePattern = new(@"""title""\s*:\s*""([^""]{1,40})""", RegexOptions.Compiled);

    public string Rewrite(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        string[] lines = message.Split('\n');
        long currentGroupId = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            Match header = GroupHeaderPattern.Match(lines[index]);
            if (header.Success)
            {
                currentGroupId = long.Parse(header.Groups["id"].Value);
                directory.LearnGroup(currentGroupId, header.Groups["name"].Value);
            }

            Match speaker = SpeakerPattern.Match(lines[index]);
            if (speaker.Success == false)
            {
                continue;
            }

            long userId = long.Parse(speaker.Groups["id"].Value);
            string eventName = speaker.Groups["name"].Value;
            string body = speaker.Groups["body"].Value;
            bool isPrivate = speaker.Groups["private"].Success;
            long groupId = isPrivate ? 0 : currentGroupId;
            bool isSelf = BotId != 0 && userId == BotId;

            string displayName = ResolveDisplayName(userId, groupId, eventName);
            string rewrittenBody = RewriteBody(body, groupId);
            string verdict = isSelf || BindMenuReplies == false || Ledger == null
                ? ""
                : MatchMenu(userId, groupId, body);

            string tag = (isPrivate ? "[私聊]" : "") + "[" + userId + (displayName.Length > 0 ? "(" + displayName + ")" : "")
                + (isSelf && MarkSelfEcho ? "·你自己" : "") + "]:";
            lines[index] = tag + rewrittenBody + (verdict.Length > 0 ? "〔" + verdict + "〕" : "");
        }

        return string.Join("\n", lines);
    }

    string ResolveDisplayName(long userId, long groupId, string eventName)
    {
        string known = directory.DisplayName(userId, groupId, PreferGroupCard);
        if (known.Length > 0)
        {
            return known;
        }
        return eventName;
    }

    string RewriteBody(string body, long groupId)
    {
        string result = NormalizeSegments ? NormalizeSegmentsOf(body) : body;
        if (ResolveAtName)
        {
            result = AtPattern.Replace(result, match => ReplaceAt(match, groupId));
        }
        return result;
    }

    string ReplaceAt(Match match, long groupId)
    {
        long userId = long.Parse(match.Groups["id"].Value);
        if (BotId != 0 && userId == BotId)
        {
            return "@你";
        }
        string name = directory.DisplayName(userId, groupId, PreferGroupCard);
        return name.Length > 0 ? "@" + name : match.Value;
    }

    string NormalizeSegmentsOf(string body)
    {
        string result = RecordPattern.Replace(body, match => Shorten("语音", match.Value));
        result = VideoPattern.Replace(result, match => Shorten("视频", match.Value));
        result = CardPattern.Replace(result, match => Shorten("卡片", match.Value));
        result = MusicPattern.Replace(result, match => Shorten("音乐分享", match.Value));
        result = LocationPattern.Replace(result, "[位置分享]");
        return result;
    }

    static string Shorten(string label, string segment)
    {
        if (label == "卡片")
        {
            Match title = CardTitlePattern.Match(segment);
            if (title.Success)
            {
                return "[卡片: " + title.Groups[1].Value + "]";
            }
        }
        Match link = LinkPattern.Match(segment);
        return link.Success
            ? "[" + label + ": " + link.Value.TrimEnd('.', ',', ';', ':', '。', '，', '、') + "]"
            : "[" + label + "]";
    }

    string MatchMenu(long userId, long groupId, string body)
    {
        MenuLedger.MatchResult? match = Ledger!.TryMatch(userId, groupId, StripAtMarkup(body));
        if (match == null)
        {
            return "";
        }
        return match.Verdict;
    }

    static string StripAtMarkup(string text)
    {
        return AtPattern.Replace(text, "").Replace("@你", "").Trim();
    }
}
