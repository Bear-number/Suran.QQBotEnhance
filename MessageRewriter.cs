using System;
using System.Text.RegularExpressions;

namespace Suran.QQBotEnhance;

/// <summary>
/// 改写进入对话的 QQ 消息文本：把发言人后面的裸 openid 换成昵称、把 @openid 换成 @名字、
/// 给群号补上已知群名、标出机器人自己的回声，并把回复对回本插件发出的编号菜单。
/// </summary>
public sealed class MessageRewriter(PersonDirectory directory)
{
    public bool ResolveAtName { get; set; } = true;
    public bool MarkSelfEcho { get; set; } = true;
    public bool BindMenuReplies { get; set; } = true;
    public string BotId { get; set; } = "";
    public MenuLedger? Ledger { get; set; }

    // 官方机器人给的是 openid：字母数字串，不是 QQ 号那样的纯数字，按数字认会一条都匹配不上
    const string OpenIdToken = "[0-9A-Za-z_-]{8,64}";

    static readonly Regex SpeakerPattern = new(
        @"^\[(?<id>" + OpenIdToken + @")(?:\((?<name>[^)]*)\))?\]:(?<body>.*)$",
        RegexOptions.Compiled);

    // 名字里带括号是常态（「研发(1)群」），所以群名用惰性匹配交给结尾的 )] 去收
    static readonly Regex SourceHeaderPattern = new(
        @"\[(?<kind>群聊消息|私聊消息)\((?<id>" + OpenIdToken + @")(?:,(?<name>.*?))?\)\]",
        RegexOptions.Compiled);

    static readonly Regex AtPattern = new(@"@(?<id>" + OpenIdToken + @")(?![0-9A-Za-z_-])", RegexOptions.Compiled);

    public string Rewrite(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        string[] lines = message.Split('\n');
        string currentGroupId = "";
        for (int index = 0; index < lines.Length; index++)
        {
            Match header = SourceHeaderPattern.Match(lines[index]);
            if (header.Success)
            {
                bool isGroupHeader = header.Groups["kind"].Value == "群聊消息";
                string headerId = header.Groups["id"].Value;
                currentGroupId = isGroupHeader ? headerId : "";
                if (isGroupHeader == false)
                {
                    continue;
                }

                string headerName = header.Groups["name"].Value;
                if (headerName.Length > 0)
                {
                    directory.LearnGroup(headerId, headerName);
                    continue;
                }
                string knownGroupName = directory.GroupName(headerId);
                if (knownGroupName.Length > 0)
                {
                    lines[index] = "[群聊消息(" + headerId + "," + knownGroupName + ")]";
                }
                continue;
            }

            Match speaker = SpeakerPattern.Match(lines[index]);
            if (speaker.Success == false)
            {
                continue;
            }

            string userId = speaker.Groups["id"].Value;
            string body = speaker.Groups["body"].Value;
            bool isSelf = BotId.Length > 0 && userId == BotId;
            directory.Learn(userId, speaker.Groups["name"].Value);

            string displayName = directory.DisplayName(userId);
            string verdict = isSelf || BindMenuReplies == false || Ledger == null
                ? ""
                : MatchMenu(userId, currentGroupId, body);
            string tag = "[" + userId + (displayName.Length > 0 ? "(" + displayName + ")" : "")
                + (isSelf && MarkSelfEcho ? "·你自己" : "") + "]:";
            lines[index] = tag + (ResolveAtName ? ReplaceAt(body) : body)
                + (verdict.Length > 0 ? "〔" + verdict + "〕" : "");
        }

        return string.Join("\n", lines);
    }

    string ReplaceAt(string body)
    {
        return AtPattern.Replace(body, match =>
        {
            string userId = match.Groups["id"].Value;
            if (BotId.Length > 0 && userId == BotId)
            {
                return "@你";
            }
            string name = directory.DisplayName(userId);
            return name.Length > 0 ? "@" + name : match.Value;
        });
    }

    string MatchMenu(string userId, string groupId, string body)
    {
        MenuLedger.MatchResult? match = Ledger!.TryMatch(userId, groupId, StripAtMarkup(body));
        return match == null ? "" : match.Verdict;
    }

    /// <summary>
    /// 去掉 @ 标记和「@你」，只留正文里真正说的那个编号；名字是改写层填进去的，本来就不参与对账。
    /// </summary>
    static string StripAtMarkup(string text)
    {
        return AtPattern.Replace(text, "").Replace("@你", "").Trim();
    }
}
