using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Suran.QQBridge;

/// <summary>
/// QQ号 → 昵称/群名片/群身份 的记忆表，兼带群号 → 群名。改名自动跟随，超量淘汰最久没出现的人。
/// </summary>
public sealed class PersonDirectory(int capacity)
{
    public sealed record MatchedPerson(long UserId, string Name, string Source, int Score);

    public sealed class StoredPerson
    {
        public long UserId { get; set; }
        public string Nickname { get; set; } = "";
        public Dictionary<long, string> Cards { get; set; } = [];
        public Dictionary<long, string> Roles { get; set; } = [];
    }

    public sealed class StoredGroup
    {
        public long GroupId { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public DateTime? LastSync { get; set; }
        public List<StoredPerson> People { get; set; } = [];
        public List<StoredGroup> Groups { get; set; } = [];
    }

    const int NameMaxLength = 50;

    static readonly JsonSerializerOptions SnapshotStyle = new() { WriteIndented = true };

    readonly Dictionary<long, string> groupNames = [];
    readonly Queue<long> groupOrder = new();
    readonly Dictionary<long, PersonRecord> people = [];
    readonly Dictionary<long, LinkedListNode<long>> recencyNodes = [];
    readonly LinkedList<long> recencyOrder = new();

    public int PersonCount => people.Count;
    public int GroupCount => groupNames.Count;
    public DateTime? LastSyncTime { get; private set; }

    public void Learn(long userId, string? nickname, long groupId, string? card)
    {
        string cleanNickname = Clean(nickname);
        string cleanCard = Clean(card);
        if (userId == 0 || (cleanNickname.Length == 0 && cleanCard.Length == 0))
        {
            return;
        }

        if (people.TryGetValue(userId, out PersonRecord? record) == false)
        {
            record = new PersonRecord();
            people[userId] = record;
            recencyOrder.AddFirst(recencyNodes[userId] = new LinkedListNode<long>(userId));
        }
        else
        {
            Touch(userId);
        }

        if (cleanNickname.Length > 0)
        {
            record.Nickname = cleanNickname;
        }
        if (groupId != 0 && cleanCard.Length > 0)
        {
            record.Cards[groupId] = cleanCard;
        }
        Trim();
    }

    public void LearnRole(long groupId, long userId, string role)
    {
        if (people.TryGetValue(userId, out PersonRecord? record))
        {
            record.Roles[groupId] = Clean(role);
        }
    }

    public void LearnGroup(long groupId, string? name)
    {
        string cleanName = Clean(name);
        if (groupId == 0 || cleanName.Length == 0)
        {
            return;
        }
        if (groupNames.TryGetValue(groupId, out string? known) == false)
        {
            groupNames[groupId] = cleanName;
            groupOrder.Enqueue(groupId);
            while (groupNames.Count > capacity && groupOrder.Count > 0)
            {
                groupNames.Remove(groupOrder.Dequeue());
            }
            return;
        }
        groupNames[groupId] = cleanName;
    }

    public void MarkSynced()
    {
        LastSyncTime = DateTime.Now;
    }

    public string DisplayName(long userId, long groupId, bool preferCard)
    {
        if (people.TryGetValue(userId, out PersonRecord? record) == false)
        {
            return "";
        }
        Touch(userId);

        if (preferCard && groupId != 0 && record.Cards.TryGetValue(groupId, out string? card) && card.Length > 0)
        {
            return card;
        }
        return record.Nickname;
    }

    public string GroupName(long groupId)
    {
        return groupNames.TryGetValue(groupId, out string? name) ? name : "";
    }

    public string RoleOf(long groupId, long userId)
    {
        if (people.TryGetValue(userId, out PersonRecord? record) && record.Roles.TryGetValue(groupId, out string? role))
        {
            return role;
        }
        return "";
    }

    public List<MatchedPerson> Search(string keyword, long groupId, int limit)
    {
        string needle = keyword.Trim();
        List<MatchedPerson> matched = [];
        foreach (KeyValuePair<long, PersonRecord> pair in people)
        {
            string card = pair.Value.Cards.TryGetValue(groupId, out string? knownCard) ? knownCard : "";
            string nickname = pair.Value.Nickname;
            string shown = card.Length > 0 ? card : nickname;

            int cardScore = ScoreName(card, needle);
            int nicknameScore = ScoreName(nickname, needle);
            int numberScore = needle.Length > 0 && pair.Key.ToString().Contains(needle) ? 4 : 0;
            int score = Math.Max(cardScore, Math.Max(nicknameScore, numberScore));
            if (score == 0)
            {
                continue;
            }

            string source = numberScore == score && numberScore > 0 ? "QQ号"
                : cardScore >= nicknameScore && card.Length > 0 ? "群名片" : "昵称";
            matched.Add(new MatchedPerson(pair.Key,
                shown.Length > 0 ? shown : "（只记到号，没记到名字）", source, score));
        }

        matched.Sort((left, right) => right.Score.CompareTo(left.Score) != 0
            ? right.Score.CompareTo(left.Score)
            : left.UserId.CompareTo(right.UserId));
        if (matched.Count > limit)
        {
            matched.RemoveRange(limit, matched.Count - limit);
        }
        return matched;
    }

    /// <summary>按名片或昵称精确找人；同名多人时返回 false，让调用方保留原文而不是@错人。</summary>
    public bool TryResolveByName(long groupId, string name, out long userId)
    {
        userId = 0;
        string wanted = name.Trim();
        if (wanted.Length == 0)
        {
            return false;
        }

        List<long> hits = [];
        foreach (KeyValuePair<long, PersonRecord> pair in people)
        {
            bool cardHit = groupId != 0 && pair.Value.Cards.TryGetValue(groupId, out string? card)
                && string.Equals(card, wanted, StringComparison.OrdinalIgnoreCase);
            bool nicknameHit = string.Equals(pair.Value.Nickname, wanted, StringComparison.OrdinalIgnoreCase);
            if (cardHit || nicknameHit)
            {
                hits.Add(pair.Key);
            }
        }
        if (hits.Count != 1)
        {
            return false;
        }
        userId = hits[0];
        return true;
    }

    public string Describe(long userId, long groupId)
    {
        if (people.TryGetValue(userId, out PersonRecord? record) == false)
        {
            return "";
        }
        List<string> parts = [];
        if (record.Nickname.Length > 0)
        {
            parts.Add("昵称 " + record.Nickname);
        }
        if (groupId != 0 && record.Cards.TryGetValue(groupId, out string? card) && card.Length > 0)
        {
            parts.Add("群名片 " + card);
        }
        if (groupId != 0 && record.Roles.TryGetValue(groupId, out string? role) && role.Length > 0)
        {
            parts.Add("身份 " + role);
        }
        return string.Join("｜", parts);
    }

    public void Save(string path)
    {
        Snapshot snapshot = new() { LastSync = LastSyncTime };
        foreach (KeyValuePair<long, PersonRecord> pair in people)
        {
            snapshot.People.Add(new StoredPerson
            {
                UserId = pair.Key,
                Nickname = pair.Value.Nickname,
                Cards = new Dictionary<long, string>(pair.Value.Cards),
                Roles = new Dictionary<long, string>(pair.Value.Roles),
            });
        }
        foreach (KeyValuePair<long, string> pair in groupNames)
        {
            snapshot.Groups.Add(new StoredGroup { GroupId = pair.Key, Name = pair.Value });
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

        foreach (StoredPerson person in snapshot.People)
        {
            PersonRecord record = new() { Nickname = Clean(person.Nickname) };
            foreach (KeyValuePair<long, string> pair in person.Cards)
            {
                record.Cards[pair.Key] = Clean(pair.Value);
            }
            foreach (KeyValuePair<long, string> pair in person.Roles)
            {
                record.Roles[pair.Key] = Clean(pair.Value);
            }
            if (person.UserId == 0 || (record.Nickname.Length == 0 && record.Cards.Count == 0))
            {
                continue;
            }
            people[person.UserId] = record;
            recencyOrder.AddFirst(recencyNodes[person.UserId] = new LinkedListNode<long>(person.UserId));
        }
        foreach (StoredGroup group in snapshot.Groups)
        {
            LearnGroup(group.GroupId, group.Name);
        }
        if (snapshot.LastSync != null)
        {
            LastSyncTime = snapshot.LastSync;
        }
        Trim();
    }

    static int ScoreName(string name, string needle)
    {
        if (name.Length == 0 || needle.Length == 0)
        {
            return 0;
        }
        if (string.Equals(name, needle, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        if (name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return 0;
    }

    static string Clean(string? value)
    {
        string trimmed = (value ?? "").Trim();
        return trimmed.Length > NameMaxLength ? trimmed[..NameMaxLength] : trimmed;
    }

    void Touch(long userId)
    {
        if (recencyNodes.TryGetValue(userId, out LinkedListNode<long>? node) == false)
        {
            recencyOrder.AddFirst(recencyNodes[userId] = new LinkedListNode<long>(userId));
            return;
        }
        if (node != recencyOrder.First)
        {
            recencyOrder.Remove(node);
            recencyOrder.AddFirst(node);
        }
    }

    void Trim()
    {
        while (recencyOrder.Last != null && people.Count > capacity)
        {
            long staleUserId = recencyOrder.Last!.Value;
            recencyOrder.RemoveLast();
            recencyNodes.Remove(staleUserId);
            people.Remove(staleUserId);
        }
    }

    sealed class PersonRecord
    {
        public string Nickname { get; set; } = "";
        public Dictionary<long, string> Cards { get; set; } = [];
        public Dictionary<long, string> Roles { get; set; } = [];
    }
}
