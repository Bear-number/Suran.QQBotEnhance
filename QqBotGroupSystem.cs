using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Suran.QQBotEnhance;

public sealed record QqBotGroupMember(string OpenId, string UserName, string MemberRole, bool IsBot, string JoinedAt);

/// <summary>
/// 官方机器人的群与成员查询。这几条大半踩在内邀/白名单上（无权限报 11253），
/// 所以每一层都要能把「查不到」和「没权限」说清楚，不能编一个看起来完整的答案。
/// </summary>
public sealed class QqBotGroupSystem(QqBotApiSystem api)
{
    // 成员列表每页最多 30 条，翻页靠 next_cursor，空串代表到底
    const int MemberPageGuard = 40;

    public async Task<QqBotApiSystem.Result> GroupInfoAsync(string groupOpenId, CancellationToken cancellationToken)
    {
        return await api.GetAsync("/v2/groups/" + QqBotApiSystem.EncodeSegment(groupOpenId) + "/info", cancellationToken);
    }

    public async Task<QqBotApiSystem.Result> BotStateAsync(string groupOpenId, CancellationToken cancellationToken)
    {
        return await api.GetAsync("/v2/groups/" + QqBotApiSystem.EncodeSegment(groupOpenId) + "/bot_state", cancellationToken);
    }

    public async Task<QqBotApiSystem.Result> MuteSettingAsync(string groupOpenId, CancellationToken cancellationToken)
    {
        return await api.GetAsync(
            "/v2/groups/" + QqBotApiSystem.EncodeSegment(groupOpenId) + "/restrict_chat_setting", cancellationToken);
    }

    /// <summary>
    /// 改禁言。官方不是「禁多少秒」而是给一个到期时刻：`mute_expire_at` 用 +08:00 的本地时间串，
    /// 解除时给空串。一次最多 20 人、上限 30 天，而且要机器人是群管理员。
    /// </summary>
    public async Task<QqBotApiSystem.Result> SetMuteAsync(
        string groupOpenId, List<(string MemberOpenId, DateTime? ExpireAtLocal)> changes, CancellationToken cancellationToken)
    {
        List<object> members = [];
        foreach ((string memberOpenId, DateTime? expireAt) in changes)
        {
            members.Add(new
            {
                op = expireAt == null ? "del" : "add",
                member_openid = memberOpenId,
                mute_expire_at = expireAt == null ? "" : FormatTimestamp(expireAt.Value),
            });
        }
        return await api.PostAsync(
            "/v2/groups/" + QqBotApiSystem.EncodeSegment(groupOpenId) + "/restrict_chat_setting",
            new { members }, cancellationToken);
    }

    public static string FormatTimestamp(DateTime localTime)
    {
        // 平台按 +08:00 解读，写成 ISO 但保留这个偏移，别让 UTC 把禁言时长差出 8 小时
        return new DateTimeOffset(localTime, TimeSpan.FromHours(8)).ToString("yyyy-MM-ddTHH:mm:sszzz",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<QqBotApiSystem.Result> MembersPageAsync(
        string groupOpenId, string cursor, CancellationToken cancellationToken)
    {
        string path = "/v2/groups/" + QqBotApiSystem.EncodeSegment(groupOpenId) + "/members";
        if (cursor.Length > 0)
        {
            path += "?cursor=" + Uri.EscapeDataString(cursor);
        }
        return await api.GetAsync(path, cancellationToken);
    }

    public static List<QqBotGroupMember> ReadMembers(JsonElement payload)
    {
        List<QqBotGroupMember> members = [];
        foreach (JsonElement item in QqBotJson.Array(payload, "members"))
        {
            string openId = QqBotJson.Text(item, "member_openid");
            if (openId.Length == 0)
            {
                continue;
            }
            members.Add(new QqBotGroupMember(
                openId,
                QqBotJson.Text(item, "username"),
                QqBotJson.Text(item, "member_role"),
                QqBotJson.Bool(item, "bot"),
                QqBotJson.Text(item, "joined_at")));
        }
        return members;
    }

    /// <summary>
    /// 翻完整个成员表。`Failure` 非空表示中途失败或被页数上限截断，调用方要照实说。
    /// </summary>
    public async Task<(List<QqBotGroupMember> Members, string Failure)> FetchAllMembersAsync(
        string groupOpenId, CancellationToken cancellationToken)
    {
        List<QqBotGroupMember> all = [];
        string cursor = "";
        for (int page = 0; page < MemberPageGuard; page++)
        {
            QqBotApiSystem.Result result = await MembersPageAsync(groupOpenId, cursor, cancellationToken);
            if (result.Data == null)
            {
                return (all, result.Failure);
            }
            all.AddRange(ReadMembers(result.Data.Value));
            cursor = QqBotJson.Text(result.Data.Value, "next_cursor");
            if (cursor.Length == 0)
            {
                return (all, "");
            }
        }
        return (all, "翻到第 " + MemberPageGuard + " 页还没到底，剩下的没取");
    }

    /// <summary>
    /// 读禁言名单里还在禁的人。`mute_expire_at` 是绝对时刻，过了就不算禁着。
    /// </summary>
    public static List<(string MemberOpenId, string UserName, DateTime ExpireAt)> ReadMuted(JsonElement payload)
    {
        List<(string MemberOpenId, string UserName, DateTime ExpireAt)> muted = [];
        DateTime now = DateTime.Now;
        foreach (JsonElement item in QqBotJson.Array(payload, "members"))
        {
            string openId = QqBotJson.Text(item, "member_openid");
            string expireText = QqBotJson.Text(item, "mute_expire_at");
            if (openId.Length == 0 || expireText.Length == 0)
            {
                continue;
            }
            if (TryParseTimestamp(expireText, out DateTime expireAt) == false || expireAt <= now)
            {
                continue;
            }
            muted.Add((openId, QqBotJson.Text(item, "username"), expireAt));
        }
        return muted;
    }

    public static bool TryParseTimestamp(string text, out DateTime value)
    {
        // 平台给的是 +08:00 的 ISO 串，本地钟点直接可读，别再转成 UTC 差出 8 小时
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out value);
    }
}
