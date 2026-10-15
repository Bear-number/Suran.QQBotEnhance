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

    // 两代 QQ聊天 的头部都得认：旧的 [Q群消息(id,群名)]，新的 [群聊消息(id,群名)] 与 [私聊消息(id,昵称)]。
    // 名字里带括号是常态（「研发(1)群」），所以用惰性匹配交给结尾的 )] 去收。
    static readonly Regex SourceHeaderPattern = new(
        @"\[(?<kind>Q群消息|群聊消息|私聊消息)\((?<id>\d+)(?:,(?<name>.*?))?\)\]",
        RegexOptions.Compiled);

    static readonly Regex AtPattern = new(@"@(?<id>\d{4,12})(?![0-9])", RegexOptions.Compiled);
    static readonly Regex AtAllPattern = new(@"\[CQ:at,.*?qq=all[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        long currentPrivateUserId = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            Match header = SourceHeaderPattern.Match(lines[index]);
            if (header.Success)
            {
                // 号码解析不出来（协议端抽风给了非数字）就把这块的来源当不知道，不能沿用上一块的
                long id = long.TryParse(header.Groups["id"].Value, out long headerId) ? headerId : 0;
                bool isPrivateHeader = header.Groups["kind"].Value == "私聊消息";
                currentGroupId = isPrivateHeader ? 0 : id;
                currentPrivateUserId = isPrivateHeader ? id : 0;
                if (isPrivateHeader)
                {
                    directory.Learn(id, header.Groups["name"].Value, 0, "");
                }
                else
                {
                    directory.LearnGroup(id, header.Groups["name"].Value);
                }
                continue;
            }

            Match speaker = SpeakerPattern.Match(lines[index]);
            if (speaker.Success)
            {
                currentPrivateUserId = 0;

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
                continue;
            }

            // 新一代的私聊缓存不带 [QQ号(昵称)]: 前缀，正文光着来，只能认着上一行的私聊头就地改写
            if (currentPrivateUserId != 0)
            {
                lines[index] = RewritePrivateLine(lines[index], currentPrivateUserId);
            }
        }

        return string.Join("\n", lines);
    }

    string RewritePrivateLine(string line, long userId)
    {
        if (line.Trim().Length == 0)
        {
            return line;
        }

        string result = RewriteBody(line, 0);
        string verdict = userId == BotId || BindMenuReplies == false || Ledger == null
            ? ""
            : MatchMenu(userId, 0, line);
        return result + (verdict.Length > 0 ? "〔" + verdict + "〕" : "");
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
        // 框架的 @过滤只认数字号，qq=all 的@全体会整条原始段留在上下文里
        string result = AtAllPattern.Replace(body, "@全体");
        result = RecordPattern.Replace(result, match => Shorten("语音", match.Value));
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
