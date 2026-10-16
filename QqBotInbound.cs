using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suran.QQBotEnhance;

public sealed record QqBotMention(string OpenId, string UserName, bool IsBot, string MemberRole);

/// <summary>
/// 官方机器人的一条入站消息：把 GROUP_AT_MESSAGE_CREATE / GROUP_MESSAGE_CREATE / C2C_MESSAGE_CREATE
/// 的事件体读成「群号 + 发言人 + 正文 + 附件」，再渲染成改写层认识的两行形状。
/// </summary>
public sealed class QqBotInbound
{
    // 入站 content 里的 @ 是平台自己下发的富文本标记：<@32位hex>，也见过发送侧规范形态 <qqbot-at-user id="…" />
    static readonly Regex AtMarkupPattern = new(
        @"<@(?<direct>[0-9A-Za-z]{8,})>|<qqbot-at-user\s+id=""(?<tagged>[0-9A-Za-z]{8,})""\s*/?>",
        RegexOptions.Compiled);

    public string EventType { get; init; } = "";
    public bool IsGroup { get; init; }
    public string GroupId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string MemberRole { get; init; } = "";
    public bool AuthorIsBot { get; init; }
    public string MessageId { get; init; } = "";
    public string Body { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public List<QqBotMention> Mentions { get; init; } = [];
    public List<string> AtIds { get; init; } = [];
    public string ConversationId => IsGroup ? GroupId : UserId;

    public static QqBotInbound? TryParse(string eventType, JsonElement data)
    {
        bool isGroup = eventType.StartsWith("GROUP_", StringComparison.Ordinal);
        if (isGroup == false && eventType != "C2C_MESSAGE_CREATE")
        {
            return null;
        }

        JsonElement author = QqBotJson.Child(data, "author");
        string userId = QqBotJson.Text(author, isGroup ? "member_openid" : "user_openid");
        if (userId.Length == 0)
        {
            userId = QqBotJson.Text(author, "id");
        }
        string groupId = isGroup ? QqBotJson.Text(data, "group_openid") : "";
        if (userId.Length == 0 || (isGroup && groupId.Length == 0))
        {
            return null;
        }

        List<string> atIds = [];
        StringBuilder body = new(FlattenAt(QqBotJson.Text(data, "content"), atIds));
        AppendAttachments(data, body);
        AppendQuotedElements(data, body);
        AppendArkCard(data, body);

        return new QqBotInbound
        {
            EventType = eventType,
            IsGroup = isGroup,
            GroupId = groupId,
            UserId = userId,
            // 私聊事件里 author.username 恒为空串，官方文档的示例就是这样，能拿到的只有 openid
            UserName = QqBotJson.Text(author, "username"),
            MemberRole = QqBotJson.Text(author, "member_role"),
            AuthorIsBot = QqBotJson.Bool(author, "bot"),
            MessageId = QqBotJson.Text(data, "id"),
            Timestamp = QqBotJson.Text(data, "timestamp"),
            Mentions = ReadMentions(data),
            AtIds = atIds,
            Body = body.ToString(),
        };
    }

    public string ToConversationText()
    {
        StringBuilder text = new();
        text.Append(IsGroup ? "[群聊消息(" : "[私聊消息(").Append(ConversationId).Append(")]\n");
        text.Append('[').Append(UserId);
        if (UserName.Length > 0)
        {
            text.Append('(').Append(UserName).Append(')');
        }
        text.Append("]: ").Append(Body);
        return text.ToString();
    }

    static string FlattenAt(string content, List<string> atIds)
    {
        if (content.Length == 0)
        {
            return content;
        }
        // 压成 @<openid> 交给改写层换成名字：名字给用户看，openid 才是身份（昵称谁都能改成机器人的名字）
        return AtMarkupPattern.Replace(content, match =>
        {
            string openId = match.Groups["direct"].Success ? match.Groups["direct"].Value : match.Groups["tagged"].Value;
            atIds.Add(openId);
            return "@" + openId;
        });
    }

    static void AppendAttachments(JsonElement data, StringBuilder body)
    {
        foreach (JsonElement item in QqBotJson.Array(data, "attachments"))
        {
            if (body.Length > 0)
            {
                body.Append(' ');
            }
            body.Append(RenderAttachment(item));
        }
    }

    static List<QqBotMention> ReadMentions(JsonElement data)
    {
        List<QqBotMention> mentions = [];
        foreach (JsonElement item in QqBotJson.Array(data, "mentions"))
        {
            string openId = QqBotJson.Text(item, "member_openid");
            if (openId.Length == 0)
            {
                openId = QqBotJson.Text(item, "user_openid");
            }
            if (openId.Length == 0)
            {
                openId = QqBotJson.Text(item, "id");
            }
            if (openId.Length == 0)
            {
                continue;
            }
            mentions.Add(new QqBotMention(
                openId, QqBotJson.Text(item, "username"), QqBotJson.Bool(item, "bot"), QqBotJson.Text(item, "member_role")));
        }
        return mentions;
    }

    static void AppendQuotedElements(JsonElement data, StringBuilder body)
    {
        // 多段消息与引用都在 msg_elements[] 里，被引用那条的作者就在第一个元素里，不用额外请求
        foreach (JsonElement element in QqBotJson.Array(data, "msg_elements"))
        {
            string content = FlattenAt(QqBotJson.Text(element, "content"), []);
            if (content.Trim().Length == 0)
            {
                continue;
            }
            string authorId = QqBotJson.Text(QqBotJson.Child(element, "author"), "member_openid");
            body.Append(authorId.Length > 0 ? "〔引用 " + authorId + "：" + content + "〕" : "〔引用：" + content + "〕");
        }
    }

    static void AppendArkCard(JsonElement data, StringBuilder body)
    {
        // 结构化卡片是 message_type=3 + ark_data{fields}，只读 content 和 attachments 的话整条消息会被读成空
        JsonElement ark = QqBotJson.Child(data, "ark_data");
        if (ark.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        JsonElement fields = QqBotJson.Child(ark, "fields");
        string kind = PickText(ark, fields, "ark_name", "ark_type");
        string title = PickText(ark, fields, "title");
        string description = PickText(ark, fields, "desc", "prompt");
        List<string> parts = [];
        if (title.Length > 0)
        {
            parts.Add(title);
        }
        if (description.Length > 0)
        {
            parts.Add(description);
        }
        string detail = string.Join(" - ", parts);
        string label = kind.Length > 0 ? kind : "卡片";
        body.Append("〔").Append(detail.Length > 0 ? label + ": " + detail : label + ": 读不出内容").Append("〕");
    }

    static string PickText(JsonElement primary, JsonElement secondary, params string[] names)
    {
        // 卡片字段在 ark_data 顶层还是 ark_data.fields 里，两种都见过，按顺序找第一个非空的
        foreach (JsonElement source in new[] { primary, secondary })
        {
            foreach (string name in names)
            {
                string text = QqBotJson.Text(source, name);
                if (text.Trim().Length > 0)
                {
                    return text.Trim();
                }
            }
        }
        return "";
    }

    public static string RenderAttachment(JsonElement attachment)
    {
        string url = QqBotJson.Text(attachment, "url");
        string contentType = QqBotJson.Text(attachment, "content_type");
        string fileName = QqBotJson.Text(attachment, "filename");
        long size = QqBotJson.Number(attachment, 0, "size");
        // 语音的 content_type 是字面量 voice 而不是 mime，别拿它当文件
        if (contentType == "voice")
        {
            string asr = QqBotJson.Text(attachment, "asr_refer_text");
            string wavUrl = QqBotJson.Text(attachment, "voice_wav_url");
            return asr.Length > 0
                ? "[语音: " + asr + "]" + (wavUrl.Length > 0 ? " " + wavUrl : "")
                : "[语音: " + PickUrl(wavUrl, url) + "]";
        }
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "[图片: " + url + "]";
        }
        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return "[视频: " + url + "]";
        }
        string humanSize = size > 0 ? " " + HumanSize(size) : "";
        return "[文件: " + (fileName.Length > 0 ? fileName : contentType) + humanSize + "] " + url;
    }

    static string PickUrl(string first, string second)
    {
        return first.Length > 0 ? first : second;
    }

    static string HumanSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        }
        if (bytes >= 1024)
        {
            return (bytes / 1024.0).ToString("0.#") + " KB";
        }
        return bytes + " B";
    }
}
