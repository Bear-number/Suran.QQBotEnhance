using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Suran.QQBotEnhance;

public sealed record QqBotSendReport(bool Ok, string Route, long ErrorCode, string Failure, string MessageId);

/// <summary>
/// 官方机器人的发送侧：文本 / markdown（带按钮）/ 富媒体，外加文件上传与互动回调应答。
/// 被动回复（带 msg_id）过期或超次数就自动退成主动消息——用户看得见的差别只是「有没有引用」，
/// 卡在这里不出话才是真正的故障。
/// </summary>
public sealed class QqBotMessageSystem(QqBotApiSystem api)
{
    const int MsgTypeText = 0;
    const int MsgTypeMarkdown = 2;
    const int MsgTypeMedia = 7;

    static readonly long[] PassiveFailureCodes = [40034005, 40034128, 40034024, 40034026, 40034027];

    // 同 msg_id + 同 msg_seq 重复发会被去重（40054005），所以每个回复目标自己记一条流水
    readonly Dictionary<string, int> sequenceByTarget = [];

    public async Task<QqBotSendReport> SendTextAsync(
        string target, bool isGroup, string text, string? replyMessageId, CancellationToken cancellationToken)
    {
        Dictionary<string, object> core = new() { ["msg_type"] = MsgTypeText, ["content"] = text };
        return await SendAsync(target, isGroup, core, replyMessageId, cancellationToken);
    }

    /// <summary>
    /// 发 markdown。官方只有 markdown 消息支持按钮，所以带键盘的文本必须走这条；
    /// 平台侧的 markdown 模板权限缺失会报错，那时调用方要退回纯文本。
    /// </summary>
    public async Task<QqBotSendReport> SendMarkdownAsync(
        string target, bool isGroup, string content, object? keyboard, string? replyMessageId, CancellationToken cancellationToken)
    {
        Dictionary<string, object> core = new() { ["msg_type"] = MsgTypeMarkdown, ["markdown"] = new { content } };
        if (keyboard != null)
        {
            core["keyboard"] = keyboard;
        }
        return await SendAsync(target, isGroup, core, replyMessageId, cancellationToken);
    }

    public async Task<QqBotSendReport> SendMediaAsync(
        string target, bool isGroup, string fileInfo, string? replyMessageId, CancellationToken cancellationToken)
    {
        Dictionary<string, object> core = new() { ["msg_type"] = MsgTypeMedia, ["media"] = new { file_info = fileInfo } };
        return await SendAsync(target, isGroup, core, replyMessageId, cancellationToken);
    }

    async Task<QqBotSendReport> SendAsync(
        string target, bool isGroup, Dictionary<string, object> core, string? replyMessageId, CancellationToken cancellationToken)
    {
        string path = BuildPath(target, isGroup, "messages");
        bool passive = string.IsNullOrEmpty(replyMessageId) == false;
        if (passive == false)
        {
            QqBotApiSystem.Result active = await api.PostAsync(path, core, cancellationToken);
            return Report(active, "主动");
        }

        string anchorMessageId = replyMessageId!;
        int sequence = NextSequence(target, anchorMessageId);
        Dictionary<string, object> withReply = new(core) { ["msg_id"] = anchorMessageId, ["msg_seq"] = sequence };
        QqBotApiSystem.Result replied = await api.PostAsync(path, withReply, cancellationToken);
        if (replied.Data != null)
        {
            return Report(replied, "被动回复");
        }
        if (IsPassiveExpired(replied.ErrorCode) == false)
        {
            return Report(replied, "被动回复");
        }
        // 回复窗口过了（群 5 分钟/5 次、单聊 60 分钟/4 次），退成主动消息重发一次
        QqBotApiSystem.Result fallback = await api.PostAsync(path, core, cancellationToken);
        QqBotSendReport activeReport = Report(fallback, "主动（回复窗口已过，自动改发）");
        return activeReport;
    }

    public async Task<QqBotApiSystem.Result> UploadAsync(
        string target, bool isGroup, int fileType, string fileName, string base64Data, CancellationToken cancellationToken)
    {
        string path = BuildPath(target, isGroup, "files");
        return await api.PostAsync(path, BuildUploadPayload(target, isGroup, fileType, fileName, base64Data), cancellationToken);
    }

    static Dictionary<string, object> BuildUploadPayload(string target, bool isGroup, int fileType, string fileName, string base64Data)
    {
        // file_name 的发放规则是实测出来的：类型4（文件）必给、类型3（语音）绝不能给（给了语音条变成文件卡片）、
        // 类型1（图片）要给（否则 GIF 被 850019 拒）、类型2（视频）不给
        Dictionary<string, object> payload = new()
        {
            ["file_type"] = fileType,
            ["file_data"] = base64Data,
            ["srv_send_msg"] = false,
        };
        if ((fileType == 4 || fileType == 1) && fileName.Length > 0)
        {
            payload["file_name"] = fileName;
        }
        payload[isGroup ? "group_openid" : "openid"] = target;
        return payload;
    }

    /// <summary>
    /// 应答按钮回调。只有 type 11（内联键盘）和 12（单聊快捷菜单）需要回，
    /// 不回客户端会一直转圈到超时；这条路径没有 /v2 前缀。
    /// </summary>
    public async Task<QqBotApiSystem.Result> AckInteractionAsync(
        string interactionId, int code, CancellationToken cancellationToken)
    {
        return await api.PutAsync("/interactions/" + QqBotApiSystem.EncodeSegment(interactionId), new { code }, cancellationToken);
    }

    static string BuildPath(string target, bool isGroup, string tail)
    {
        return isGroup
            ? "/v2/groups/" + QqBotApiSystem.EncodeSegment(target) + "/" + tail
            : "/v2/users/" + QqBotApiSystem.EncodeSegment(target) + "/" + tail;
    }

    int NextSequence(string target, string messageId)
    {
        string key = target + "|" + messageId;
        int next = sequenceByTarget.TryGetValue(key, out int used) ? used + 1 : 1;
        sequenceByTarget[key] = next;
        if (sequenceByTarget.Count > 512)
        {
            // 只给窗口期内的 msg_id 计数，攒多了说明都是些不会再用的旧回复
            sequenceByTarget.Clear();
        }
        return next;
    }

    static bool IsPassiveExpired(long errorCode)
    {
        return PassiveFailureCodes.Contains(errorCode);
    }

    static QqBotSendReport Report(QqBotApiSystem.Result result, string route)
    {
        string messageId = result.Data == null ? "" : QqBotJson.Text(result.Data.Value, "id");
        return new QqBotSendReport(result.Data != null, route, result.ErrorCode, result.Failure, messageId);
    }
}
