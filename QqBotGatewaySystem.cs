using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Suran.QQBotEnhance;

/// <summary>
/// 官方机器人的事件长连接（网关）：拿 wss 地址、IDENTIFY 上线、按 Hello 给的周期心跳、把 DISPATCH 原样递出去。
/// 重连一律重发 IDENTIFY 而不是 RESUME——RESUME 不带 intents，续上的会话收不到消息却看着像在线，
/// 源插件就靠「清掉 session_id 再断线」强制走 IDENTIFY 才把新加的事件位生效。
/// </summary>
public sealed class QqBotGatewaySystem : IAsyncDisposable
{
    public sealed record Event(string Type, JsonElement Data, long Sequence);

    const int OpCodeDispatch = 0;
    const int OpCodeHeartbeat = 1;
    const int OpCodeIdentify = 2;
    const int OpCodeReconnect = 7;
    const int OpCodeInvalidSession = 9;
    const int OpCodeHello = 10;
    const int OpCodeHeartbeatAck = 11;

    const int ReceiveBufferSize = 16 * 1024;
    const int MaxBackoffSeconds = 60;

    // 官方关闭码表里标了「IDENTIFY 不能重试」的那些：配置错了重连也白重连，必须停下来让人去看
    static readonly HashSet<int> FatalCloseCodes = [4001, 4002, 4010, 4011, 4012, 4013, 4014];

    static readonly Dictionary<int, string> CloseCodeMeanings = new()
    {
        [4001] = "发了网关不认的 opcode",
        [4002] = "IDENTIFY 的内容不合法",
        [4006] = "session_id 无效，只能重新鉴权",
        [4007] = "心跳带的 seq 错了",
        [4008] = "发包太快，被频控",
        [4009] = "连接活得太久被判过期",
        [4010] = "shard 不合法",
        [4011] = "这个分片要接的频道太多，需要真的分片",
        [4012] = "version 不对",
        [4013] = "intent 不合法",
        [4014] = "订阅的事件没有权限（内邀/白名单位）",
    };

    readonly QqBotApiSystem api;
    readonly long intents;
    readonly CancellationTokenSource lifetimeSource = new();

    ClientWebSocket? socket;
    Task? runnerTask;
    long lastSequence;
    long heartbeatIntervalMs;
    bool heartbeatAcked = true;
    bool identified;
    bool isFatalFailure;
    string lastFailure = "";

    public QqBotGatewaySystem(QqBotApiSystem api, long intents)
    {
        this.api = api;
        this.intents = intents;
    }

    public event Action<Event>? EventReceived;
    public event Action<string>? ConnectionFailed;

    public bool IsConnected => identified && socket?.State == WebSocketState.Open;

    public bool StoppedByPlatform => isFatalFailure;

    public string StateText
    {
        get
        {
            if (runnerTask == null)
            {
                return "还没启动";
            }
            if (IsConnected)
            {
                return "在线，事件序号 " + lastSequence + "，订阅位 " + intents;
            }
            if (isFatalFailure)
            {
                return "被平台拒绝，已停止重连：" + lastFailure;
            }
            return "离线（" + (lastFailure.Length == 0 ? "正在重连" : lastFailure) + "）";
        }
    }

    public void Start()
    {
        if (runnerTask != null)
        {
            return;
        }
        runnerTask = Task.Run(RunAsync, lifetimeSource.Token);
    }

    public async ValueTask DisposeAsync()
    {
        lifetimeSource.Cancel();
        Task? current = runnerTask;
        if (current != null)
        {
            await Task.WhenAll(current).ConfigureAwait(false);
        }
        lifetimeSource.Dispose();
    }

    async Task RunAsync()
    {
        CancellationToken cancellationToken = lifetimeSource.Token;
        int backoffSeconds = 1;
        while (cancellationToken.IsCancellationRequested == false)
        {
            try
            {
                await ServeAsync(cancellationToken);
                // 一轮会话正常跑完说明链路是通的，退避从头再来
                backoffSeconds = 1;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception serveError)
            {
                RecordFailure(serveError.Message);
            }
            if (isFatalFailure)
            {
                return;
            }
            if (await SleepAsync(backoffSeconds, cancellationToken) == false)
            {
                return;
            }
            backoffSeconds = Math.Min(backoffSeconds * 2, MaxBackoffSeconds);
        }
    }

    async Task ServeAsync(CancellationToken cancellationToken)
    {
        string gatewayUrl = await ResolveGatewayUrlAsync(cancellationToken);
        using ClientWebSocket connection = new();
        connection.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await connection.ConnectAsync(new Uri(gatewayUrl), cancellationToken);

        socket = connection;
        identified = false;
        lastSequence = 0;
        heartbeatIntervalMs = 0;
        heartbeatAcked = true;

        using CancellationTokenSource sessionSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeats = KeepHeartbeatAsync(connection, sessionSource);
        try
        {
            await ReceiveAsync(connection, sessionSource.Token, cancellationToken);
        }
        finally
        {
            sessionSource.Cancel();
            await IgnoreTaskAsync(heartbeats);
            socket = null;
            identified = false;
        }
    }

    async Task ReceiveAsync(ClientWebSocket connection, CancellationToken sessionToken, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        while (connection.State == WebSocketState.Open && cancellationToken.IsCancellationRequested == false)
        {
            using MemoryStream frame = new();
            WebSocketReceiveResult result;
            do
            {
                result = await connection.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    HandleClose(result.CloseStatus);
                    return;
                }
                frame.Write(buffer, 0, result.Count);
            }
            while (result.EndOfMessage == false);

            await HandleFrameAsync(Encoding.UTF8.GetString(frame.ToArray()), connection, cancellationToken);
            if (sessionToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    async Task HandleFrameAsync(string text, ClientWebSocket connection, CancellationToken cancellationToken)
    {
        JsonElement envelope;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            envelope = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            RecordFailure("网关发来一段不是 JSON 的帧，长度 " + text.Length);
            return;
        }

        int opCode = QqBotJson.Integer(envelope, -1, "op");
        long sequence = QqBotJson.Number(envelope, 0, "s");
        if (sequence > lastSequence)
        {
            lastSequence = sequence;
        }

        if (opCode == OpCodeHello)
        {
            heartbeatIntervalMs = QqBotJson.Number(QqBotJson.Child(envelope, "d"), 0, "heartbeat_interval");
            heartbeatAcked = true;
            await IdentifyAsync(connection, cancellationToken);
            return;
        }
        if (opCode == OpCodeDispatch)
        {
            await OnDispatchAsync(envelope);
            return;
        }
        if (opCode == OpCodeHeartbeatAck)
        {
            heartbeatAcked = true;
            return;
        }
        if (opCode == OpCodeReconnect)
        {
            throw new Exception("网关要求重连（op=7）");
        }
        if (opCode == OpCodeInvalidSession)
        {
            throw new Exception("会话被判无效（op=9），AppSecret 改过或平台侧踢了这条连接");
        }
    }

    async Task IdentifyAsync(ClientWebSocket connection, CancellationToken cancellationToken)
    {
        string? token = await api.GetAccessTokenAsync(cancellationToken);
        if (token == null)
        {
            throw new Exception("网关上线失败：拿不到 access_token");
        }
        object payload = BuildIdentifyPayload(token, intents);
        await SendAsync(connection, JsonSerializer.Serialize(payload), cancellationToken);
    }

    // 文档里 properties 的键名带 $（$os/$browser/$device），写成普通键名平台不一定认，
    // 而且这条帧只有连上网关才看得见，所以单独拆出来好离线核对
    public static Dictionary<string, object> BuildIdentifyPayload(string accessToken, long intentBits)
    {
        return new Dictionary<string, object>
        {
            ["op"] = OpCodeIdentify,
            ["d"] = new Dictionary<string, object>
            {
                ["token"] = "QQBot " + accessToken,
                ["intents"] = intentBits,
                ["shard"] = new[] { 0, 1 },
                ["properties"] = new Dictionary<string, object>
                {
                    ["$os"] = "windows",
                    ["$browser"] = "Suran.QQBotEnhance",
                    ["$device"] = "Suran.QQBotEnhance",
                },
            },
        };
    }

    async Task OnDispatchAsync(JsonElement envelope)
    {
        string type = QqBotJson.Text(envelope, "t");
        if (type == "READY")
        {
            identified = true;
            isFatalFailure = false;
            lastFailure = "";
            return;
        }
        if (type.Length == 0 || type == "RESUMED")
        {
            return;
        }
        JsonElement data = QqBotJson.Child(envelope, "d");
        if (data.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }
        EventReceived?.Invoke(new Event(type, data, QqBotJson.Number(envelope, 0, "s")));
    }

    async Task KeepHeartbeatAsync(ClientWebSocket connection, CancellationTokenSource sessionSource)
    {
        CancellationToken cancellationToken = sessionSource.Token;
        while (cancellationToken.IsCancellationRequested == false)
        {
            long interval = heartbeatIntervalMs;
            if (interval <= 0)
            {
                // Hello 还没来（或没给周期），先等着，别按猜出来的节奏发
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            if (heartbeatAcked == false)
            {
                // 上一拍没回：这条连接看着开着但已经收不到东西，掐掉让外层重连
                RecordFailure("心跳没有回应，平台这条连接已经哑了");
                sessionSource.Cancel();
                return;
            }
            heartbeatAcked = false;
            await SendAsync(connection, JsonSerializer.Serialize(BuildHeartbeatPayload(lastSequence)), cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(interval), cancellationToken);
        }
    }

    // 心跳要带回收到的最新序号（4007 就是 seq 错了），这条也是连上才看得见的帧，拆出来离线核
    public static Dictionary<string, object> BuildHeartbeatPayload(long lastSequenceNumber)
    {
        return new Dictionary<string, object> { ["op"] = OpCodeHeartbeat, ["d"] = lastSequenceNumber };
    }

    void HandleClose(WebSocketCloseStatus? closeStatus)
    {
        int code = closeStatus == null ? 0 : (int)closeStatus.Value;
        string meaning = CloseCodeMeanings.TryGetValue(code, out string? described) ? described : "网关关掉了这条连接";
        if (FatalCloseCodes.Contains(code))
        {
            isFatalFailure = true;
            RecordFailure("平台拒绝连接（" + code + "）：" + meaning + "，改配置才会好，重连没有用");
            return;
        }
        RecordFailure("网关断开（" + code + "）：" + meaning);
    }

    async Task<string> ResolveGatewayUrlAsync(CancellationToken cancellationToken)
    {
        // /gateway/bot 顺带给分片数与并发上限；这条不通再退到只要地址的 /gateway
        QqBotApiSystem.Result withShards = await api.GetAsync("/gateway/bot", cancellationToken);
        string url = withShards.Data == null ? "" : QqBotJson.Text(withShards.Data.Value, "url");
        if (url.Length > 0)
        {
            return url;
        }
        QqBotApiSystem.Result plain = await api.GetAsync("/gateway", cancellationToken);
        string fallbackUrl = plain.Data == null ? "" : QqBotJson.Text(plain.Data.Value, "url");
        if (fallbackUrl.Length == 0)
        {
            throw new Exception("拿不到网关地址：" + (plain.Failure.Length > 0 ? plain.Failure : withShards.Failure));
        }
        return fallbackUrl;
    }

    static async Task SendAsync(ClientWebSocket connection, string text, CancellationToken cancellationToken)
    {
        if (connection.State != WebSocketState.Open)
        {
            return;
        }
        await connection.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
    }

    async Task<bool> SleepAsync(int seconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    static async Task IgnoreTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 心跳任务带出来的原因已经由 RecordFailure 报过，这里再抛只会留一条没人看的失败任务
        }
    }

    void RecordFailure(string detail)
    {
        lastFailure = detail;
        ConnectionFailed?.Invoke(detail);
    }
}
