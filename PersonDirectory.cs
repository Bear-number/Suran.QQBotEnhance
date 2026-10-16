using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Suran.QQBotEnhance;

/// <summary>
/// openid → 昵称/群内身份 的记忆表，兼带 group_openid → 群名。改名自动跟随，超量淘汰最久没出现的人。
/// 官方机器人没有「群名片」这个概念（只有入群时填的 username），所以名片那一层整个不再存在。
/// </summary>
public sealed class PersonDirectory(int capacity)
{
    public sealed record MatchedPerson(string UserId, string Name, string Source, int Score);

    public sealed class StoredPerson
    {
        public string UserId { get; set; } = "";
        public string Nickname { get; set; } = "";
        public Dictionary<string, string> Roles { get; set; } = [];
    }

    public sealed class StoredGroup
    {
        public string GroupId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class Snapshot
    {
        public int Version { get; set; } = 2;
        public DateTime? LastSync { get; set; }
        public List<StoredPerson> People { get; set; } = [];
        public List<StoredGroup> Groups { get; set; } = [];
    }

    const int NameMaxLength = 50;

    static readonly JsonSerializerOptions SnapshotStyle = new() { WriteIndented = true };

    readonly Dictionary<string, string> groupNames = [];
    readonly Queue<string> groupOrder = new();
    readonly Dictionary<string, PersonRecord> people = [];
    readonly Dictionary<string, LinkedListNode<string>> recencyNodes = [];
    readonly LinkedList<string> recencyOrder = new();

    public int PersonCount => people.Count;
    public int GroupCount => groupNames.Count;
    public DateTime? LastSyncTime { get; private set; }

    public void Learn(string userId, string? nickname)
    {
        string cleanNickname = Clean(nickname);
        if (userId.Length == 0 || cleanNickname.Length == 0)
        {
            return;
        }

        if (people.TryGetValue(userId, out PersonRecord? record) == false)
        {
            record = new PersonRecord();
            people[userId] = record;
            recencyOrder.AddFirst(recencyNodes[userId] = new LinkedListNode<string>(userId));
        }
        else
        {
            Touch(userId);
        }
        // 每次见到新名字都覆盖：昵称是用户随时能改的，只有 openid 才是身份
        record.Nickname = cleanNickname;
        Trim();
    }

    public void LearnRole(string groupId, string userId, string role)
    {
        if (groupId.Length == 0 || role.Length == 0)
        {
            return;
        }
        if (people.TryGetValue(userId, out PersonRecord? record))
        {
            record.Roles[groupId] = Clean(role);
        }
    }

    public void LearnGroup(string groupId, string? name)
    {
        string cleanName = Clean(name);
        if (groupId.Length == 0 || cleanName.Length == 0)
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

    public string DisplayName(string userId)
    {
        if (people.TryGetValue(userId, out PersonRecord? record) == false)
        {
            return "";
        }
        Touch(userId);
        return record.Nickname;
    }

    public string GroupName(string groupId)
    {
        return groupNames.TryGetValue(groupId, out string? name) ? name : "";
    }

    /// <summary>本地记到的群号。官方没有「列出机器人所在群」的接口，能报的只有这些。</summary>
    public List<string> KnownGroupIds()
    {
        return [.. groupNames.Keys];
    }

    public string RoleOf(string groupId, string userId)
    {
        if (people.TryGetValue(userId, out PersonRecord? record) && record.Roles.TryGetValue(groupId, out string? role))
        {
            return role;
        }
        return "";
    }

    public List<MatchedPerson> Search(string keyword, int limit)
    {
        string needle = keyword.Trim();
        List<MatchedPerson> matched = [];
        foreach (KeyValuePair<string, PersonRecord> pair in people)
        {
            string nickname = pair.Value.Nickname;
            int nameScore = ScoreName(nickname, needle);
            // openid 是十六进制串，模型偶尔会只记得中间一段，允许这样捞
            int numberScore = needle.Length > 0 && pair.Key.Contains(needle, StringComparison.OrdinalIgnoreCase) ? 4 : 0;
            int score = Math.Max(nameScore, numberScore);
            if (score == 0)
            {
                continue;
            }

            string source = numberScore == score && numberScore > 0 ? "openid" : "昵称";
            matched.Add(new MatchedPerson(pair.Key,
                nickname.Length > 0 ? nickname : "（只记到号，没记到名字）", source, score));
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

    /// <summary>按昵称精确找人；同名多人时返回 false，让调用方保留原文而不是@错人。</summary>
    public bool TryResolveByName(string name, out string userId)
    {
        userId = "";
        string wanted = name.Trim();
        if (wanted.Length == 0)
        {
            return false;
        }

        List<string> hits = [];
        foreach (KeyValuePair<string, PersonRecord> pair in people)
        {
            if (string.Equals(pair.Value.Nickname, wanted, StringComparison.OrdinalIgnoreCase))
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

    public string Describe(string userId, string groupId)
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
        if (groupId.Length > 0 && record.Roles.TryGetValue(groupId, out string? role) && role.Length > 0)
        {
            parts.Add("群内身份 " + role);
        }
        return string.Join("｜", parts);
    }

    public void Save(string path)
    {
        Snapshot snapshot = new() { LastSync = LastSyncTime };
        foreach (KeyValuePair<string, PersonRecord> pair in people)
        {
            snapshot.People.Add(new StoredPerson
            {
                UserId = pair.Key,
                Nickname = pair.Value.Nickname,
                Roles = new Dictionary<string, string>(pair.Value.Roles),
            });
        }
        foreach (KeyValuePair<string, string> pair in groupNames)
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
            foreach (KeyValuePair<string, string> pair in person.Roles)
            {
                record.Roles[pair.Key] = Clean(pair.Value);
            }
            if (person.UserId.Length == 0 || (record.Nickname.Length == 0 && record.Roles.Count == 0))
            {
                continue;
            }
            people[person.UserId] = record;
            recencyOrder.AddFirst(recencyNodes[person.UserId] = new LinkedListNode<string>(person.UserId));
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

    void Touch(string userId)
    {
        if (recencyNodes.TryGetValue(userId, out LinkedListNode<string>? node) == false)
        {
            recencyOrder.AddFirst(recencyNodes[userId] = new LinkedListNode<string>(userId));
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
            string staleUserId = recencyOrder.Last!.Value;
            recencyOrder.RemoveLast();
            recencyNodes.Remove(staleUserId);
            people.Remove(staleUserId);
        }
    }

    sealed class PersonRecord
    {
        public string Nickname { get; set; } = "";
        public Dictionary<string, string> Roles { get; set; } = [];
    }
}
