using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Suran.QQBotEnhance;

/// <summary>
/// QQ 开放平台官方机器人（bot.qq.com 那套）的 HTTPS 调用层：自己管 access_token 的续期，
/// 失败判定只认 err_code（官方明确说过 message 随时会变）。
/// </summary>
public sealed class QqBotApiSystem : IAsyncDisposable
{
    public sealed record Result(JsonElement? Data, long ErrorCode, string Failure, int StatusCode);

    const string PrimaryHost = "https://api.bot.qq.com";
    const string BackupTokenHost = "https://bots.qq.com";
    const string TokenPath = "/app/getAppAccessToken";

    // token 生命周期默认 7200 秒，且「重复请求不会刷新」；官方只保证到期前 60 秒内换新 token 时旧的还有效，
    // 所以留 5 分钟的余量，别卡在那 60 秒上
    const int RefreshMarginSeconds = 300;

    readonly HttpClient httpClient;
    readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };
    readonly SemaphoreSlim tokenLock = new(1, 1);

    string accessToken = "";
    DateTime expireAtUtc = DateTime.MinValue;

    public QqBotApiSystem(string appId, string clientSecret)
    {
        this.appId = appId;
        this.clientSecret = clientSecret;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    readonly string appId;
    readonly string clientSecret;

    public bool HasCredentials => appId.Length > 0 && clientSecret.Length > 0;

    public string TokenState
    {
        get
        {
            if (HasCredentials == false)
            {
                return "没配 AppID/AppSecret";
            }
            if (accessToken.Length == 0)
            {
                return "还没拿到";
            }
            double remainSeconds = (expireAtUtc - DateTime.UtcNow).TotalSeconds;
            return remainSeconds > 0
                ? "有效，剩 " + ((int)remainSeconds).ToString(CultureInfo.InvariantCulture) + " 秒"
                : "已过期，下次调用会重新取";
        }
    }

    public async Task<Result> GetAsync(string path, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Get, path, null, cancellationToken);
    }

    public async Task<Result> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Post, path, body, cancellationToken);
    }

    public async Task<Result> PutAsync(string path, object? body, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, path, body, cancellationToken);
    }

    public async Task<Result> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Delete, path, null, cancellationToken);
    }

    public static string EncodeSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    async Task<Result> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        string? token = await GetAccessTokenAsync(cancellationToken);
        if (token == null)
        {
            return new Result(null, -1, "拿不到 access_token（AppID 或 AppSecret 不对、或者开放平台那边不通）", 0);
        }

        Result first = await SendWithTokenAsync(method, path, body, token, cancellationToken);
        if (first.StatusCode != (int)HttpStatusCode.Unauthorized)
        {
            return first;
        }
        // token 在平台侧提前失效（改密钥、后台重启）时本地那条过期时间不作数，强制重取一次再试
        InvalidateToken();
        string? refreshed = await GetAccessTokenAsync(cancellationToken);
        if (refreshed == null)
        {
            return first;
        }
        return await SendWithTokenAsync(method, path, body, refreshed, cancellationToken);
    }

    async Task<Result> SendWithTokenAsync(
        HttpMethod method, string path, object? body, string token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, PrimaryHost + path);
        request.Headers.TryAddWithoutValidation("Authorization", "QQBot " + token);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            Result result = Interpret((int)response.StatusCode, text);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception sendError)
        {
            return new Result(null, -1, "请求没发出去：" + sendError.Message, 0);
        }
    }

    Result Interpret(int statusCode, string text)
    {
        JsonElement? payload = ParseJson(text);
        if (payload == null)
        {
            if (statusCode == (int)HttpStatusCode.OK)
            {
                return new Result(null, -1, "官方返回的不是 JSON（HTTP " + statusCode + "）：" + Shorten(text), statusCode);
            }
            return new Result(null, -1, DescribeStatus(statusCode, ""), statusCode);
        }

        long errorCode = ReadErrorCode(payload.Value);
        if (statusCode != (int)HttpStatusCode.OK || errorCode != 0)
        {
            string message = QqBotJson.Text(payload.Value, "message");
            return new Result(null, errorCode, DescribeStatus(statusCode, message) + "〔err_code=" + errorCode + "〕", statusCode);
        }
        return new Result(payload, 0, "", statusCode);
    }

    string DescribeStatus(int statusCode, string message)
    {
        string detail = message.Length > 0 ? message : "官方没给说明";
        return statusCode switch
        {
            401 => "鉴权被拒（token 或 AppID/AppSecret 有问题）：" + detail,
            403 => "没有这个接口的权限，内邀/白名单类的接口要单独申请：" + detail,
            404 => "接口地址不对，这条在当前官方版本里不存在：" + detail,
            429 => "被限流了，官方要求退避后重试：" + detail,
            500 or 502 or 503 => "官方服务端出错（HTTP " + statusCode + "），稍后重试：" + detail,
            _ => "HTTP " + statusCode + "：" + detail,
        };
    }

    // 网关那边也要同一个 token（IDENTIFY 的 d.token 就是 "QQBot {access_token}"），所以这里公开
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (accessToken.Length > 0 && DateTime.UtcNow < expireAtUtc.AddSeconds(-RefreshMarginSeconds))
            {
                return accessToken;
            }
            if (HasCredentials == false)
            {
                return null;
            }

            // 文档里这个接口同时出现过 api.bot.qq.com 和 bots.qq.com 两个地址，正文表格写前者、示例 curl 写后者，
            // 所以主地址失败就退到另一个，别让用户卡在「AppID 明明是对的」上
            string? token = await RequestTokenAsync(PrimaryHost, cancellationToken);
            if (token == null)
            {
                token = await RequestTokenAsync(BackupTokenHost, cancellationToken);
            }
            return token;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    async Task<string?> RequestTokenAsync(string host, CancellationToken cancellationToken)
    {
        object payload = new { appId, clientSecret };
        using HttpRequestMessage request = new(HttpMethod.Post, host + TokenPath);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        string text;
        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        JsonElement? payloadJson = ParseJson(text);
        if (payloadJson == null)
        {
            return null;
        }
        string token = QqBotJson.Text(payloadJson.Value, "access_token");
        if (token.Length == 0)
        {
            return null;
        }
        // 示例里 expires_in 有时是数字有时是字符串，两种都吃
        int expiresIn = QqBotJson.Integer(payloadJson.Value, 7200, "expires_in");
        accessToken = token;
        expireAtUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(expiresIn, 60, 7200));
        return accessToken;
    }

    public void InvalidateToken()
    {
        accessToken = "";
        expireAtUtc = DateTime.MinValue;
    }

    JsonElement? ParseJson(string text)
    {
        if (text.Trim().Length == 0)
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static long ReadErrorCode(JsonElement payload)
    {
        // v2 用 err_code，旧的频道接口用 code，两处都读到一个算一个
        long errorCode = QqBotJson.Number(payload, -1, "err_code");
        if (errorCode != -1)
        {
            return errorCode;
        }
        return QqBotJson.Number(payload, 0, "code");
    }

    static string Shorten(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..160] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        tokenLock.Dispose();
        await Task.CompletedTask;
    }
}
