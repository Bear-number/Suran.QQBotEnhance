using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.QQBotEnhance;

[Module("QQ官方机器人聊天增强",
    "自己接 QQ 开放平台官方机器人（bot.qq.com，API v2）的收发通道：AppID/AppSecret 换 access_token，开一条网关长连接收事件，群和单聊的消息都收得到、也回得出去。进对话的消息补上昵称、把 openid 换成名字、把回复对回发出去的编号菜单；能读聊天附件正文、按名字找人、查群与禁言状态、发长内容自动分段排版，还能发带按钮的菜单。不依赖 QQ聊天（OneBot）插件，也不需要 NapCat / LLOneBot / SnowLuma 这类协议端",
    defaultCategory: "苏染的工具",
    LaunchOrder = 9000)]
public class QQBotEnhanceModule(
    XmlFunctionCaller functionCaller,
    ILogger<QQBotEnhanceModule> logger,
    Interactor<QQBotEnhanceModule> interactor) : ChatBehaviour, IConfigurable<QQBotEnhanceConfig>
{
    public QQBotEnhanceConfig Configuration { get; set; } = null!;

    // 单条上限取官方文档里的消息长度红线，超了会被平台截断或拒发
    const int SegmentCharLimit = 4000;
    const int SegmentGapMilliseconds = 600;
    const int DefaultFileFetchTimeoutSeconds = 20;
    // 各类型文件的体积红线文档没写全（只见到 850031「体积超限」），先按 20MB 收，超了如实报错而不是发一半
    const int MaxMediaBytes = 20 * 1024 * 1024;

    // 官方按钮键盘的硬限制：5 行 × 5 列、action.data ≤ 100 字、style 只认 0/1，标签过长会被 305007/40034029 打回
    const int MaxKeyboardRows = 5;
    const int MaxButtonsPerRow = 5;
    const int ButtonLabelMaxLength = 20;

    const int ReplyAnchorCapacity = 512;
    const int SeenMessageCapacity = 1024;

    // intent 位：25 是消息（C2C/群聊），24/26/27 都是权限位，没开通就订阅会被平台直接断开（关闭码 4014）
    const long IntentGroupAndC2cMessages = 1L << 25;
    const long IntentGroupMemberEvents = 1L << 24;
    const long IntentInteractionEvents = 1L << 26;
    const long IntentMessageAuditEvents = 1L << 27;

    const int MediaFileTypeImage = 1;
    const int MediaFileTypeVideo = 2;
    const int MediaFileTypeVoice = 3;
    const int MediaFileTypeFile = 4;

    // 被动回复窗口按文档：群 5 分钟、单聊 60 分钟；过窗后发送层自动退成主动消息
    static readonly TimeSpan GroupReplyWindow = TimeSpan.FromMinutes(5);
    static readonly TimeSpan PrivateReplyWindow = TimeSpan.FromMinutes(60);

    static readonly string[] VoiceSuffixes = [".silk", ".mp3", ".wav", ".ogg"];

    static readonly string[] BinarySuffixes =
    [
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".exe", ".dll", ".so", ".apk", ".ipa",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".mp3", ".mp4", ".wav", ".silk", ".flac", ".m4a", ".aac", ".ogg",
    ];

    static readonly HttpClient downloadClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    static readonly Regex OutgoingAtPattern = new(@"@([\p{L}\p{N}_\-·]{1,20})", RegexOptions.Compiled);
    static readonly Regex OutgoingOpenIdPattern = new(@"@(?<id>[0-9A-Za-z_-]{8,64})(?![0-9A-Za-z_-])", RegexOptions.Compiled);
    static readonly Regex OutgoingAtTagPattern = new(@"<qqbot-at-user\s+id=""([0-9A-Za-z_-]{8,64})""\s*/?>", RegexOptions.Compiled);
    static readonly Regex OpenIdShape = new(@"^[0-9A-Za-z_-]{8,64}$", RegexOptions.Compiled);
    static readonly Regex NumberedOptionPattern = new(@"^\d+\s*[.、)）:：]\s*", RegexOptions.Compiled);
    static readonly Regex BulletedOptionPattern = new(@"^[-*·]\s*", RegexOptions.Compiled);

    PersonDirectory directory = null!;
    MenuLedger ledger = null!;
    MessageRewriter rewriter = null!;
    QqBotApiSystem api = null!;
    QqBotMessageSystem messages = null!;
    QqBotGroupSystem groupSystem = null!;
    QqBotGatewaySystem? gateway;
    // 事件跑在 WebSocket 接收线程上，工具跑在模型那边，通讯录/台账/锚点/计数器都得过这把锁
    readonly object stateGate = new();
    // 网关事件按到达顺序排队处理：一条消息要等上一轮的模型回合结束，不然会把对话打断成一团
    readonly object eventChainGate = new();
    readonly Dictionary<string, Anchor> anchorByTarget = [];
    readonly HashSet<string> seenMessageIds = [];
    readonly Queue<string> seenMessageOrder = new();
    Task eventChain = Task.CompletedTask;
    string directoryFilePath = "";
    string menuFilePath = "";
    string botId = "";
    bool botIdGuessed;
    string lastGatewayFailure = "";
    string lastSendFailure = "";
    int inboundCount;
    int sentCount;
    bool rewriteFailureReported;

    protected override Task OnAwake()
    {
        int capacity = Math.Clamp(Configuration.DirectoryCapacity, 256, 200000);
        directory = new PersonDirectory(capacity);
        ledger = new MenuLedger(Math.Clamp(Configuration.MenuCapacity, 20, 2000));
        rewriter = new MessageRewriter(directory) { Ledger = ledger };
        api = new QqBotApiSystem(Configuration.AppId, Configuration.AppSecret);
        messages = new QqBotMessageSystem(api);
        groupSystem = new QqBotGroupSystem(api);

        directoryFilePath = Path.Combine(AlifePath.StorageFolderPath, "QQBotEnhance", "directory.json");
        menuFilePath = Path.Combine(AlifePath.StorageFolderPath, "QQBotEnhance", "menu.json");
        if (Configuration.PersistDirectory)
        {
            directory.Load(directoryFilePath);
        }
        if (Configuration.PersistMenu)
        {
            ledger.Load(menuFilePath);
        }

        XmlHandler handler = new(this)
        {
            Description = "QQ 官方机器人聊天增强：自己接 bot.qq.com 的官方机器人接口，读清楚收到的消息、按名字找人、查群与禁言状态、读聊天附件、发长内容自动分段、发编号菜单或带按钮的菜单并对账。",
            Explanation = "先决条件是在本插件设置里填 QQ 开放平台的 AppID 和 AppSecret，不填什么都连不上；填了插件自己取 access_token、自己开网关长连接，不需要 QQ聊天（OneBot）插件或任何协议端。"
                + "进来的消息自动改写：[openid(昵称)] 是发言人，@ 后面是名字，[语音: 识别文字]、[图片: 链接]、[文件: 名字 大小] 链接、〔引用〕、〔卡片〕是压缩过的原始载荷，"
                + "结尾〔〕里是编号菜单的对账结果。"
                + "要回 QQ 必须用工具发出去：正文用 send_rich（自动分段、写 @名字 会自动换成真@），图片/语音/文件用 send_media，给人挑选用 create_menu（buttons=true 是官方按钮，点了会回一条 [按钮] 事件）。"
                + "直接写在这条回复里的话 QQ 那边看不见。"
                + "机器人的 openid 是收到第一条 @ 或第一次 sync_group_members 之后才知道的；本地记到哪些群也只看通讯录，官方没有「列出机器人所在群」的接口。"
                + "群资料、群成员、机器人在群里的状态这几条接口在内邀/白名单里，没开通会直接报 11253，插件会原话转告。"
                + "禁言、踢人、公告、入群审批这些改动群状态的动作在「群管」插件里，本插件只负责查和发。",
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        functionCaller.AddPlainAreas("send_rich", "send_media", "create_menu");
        interactor.Prompt(RewriteGuide);

        StartGateway();
        logger.LogInformation("[QQ增强] 已就绪：通讯录 {People} 人、{Groups} 个群，台账 {Menus} 个菜单（10 个工具）",
            directory.PersonCount, directory.GroupCount, ledger.Count);
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        if (gateway != null)
        {
            gateway.EventReceived -= OnGatewayEvent;
            gateway.ConnectionFailed -= OnConnectionFailed;
            await gateway.DisposeAsync();
            gateway = null;
        }
        // 先落盘再释放：DisposeAsync 抛异常不能把通讯录和台账一起带走
        lock (stateGate)
        {
            if (Configuration.PersistDirectory)
            {
                directory.Save(directoryFilePath);
            }
            if (Configuration.PersistMenu)
            {
                ledger.Save(menuFilePath);
            }
        }
        await api.DisposeAsync();
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot, "bridge_state")]
    [Description("看官方机器人这条路通不通：网关连接状态、access_token 能不能取到、订阅了哪些事件、机器人自己的 openid、通讯录与菜单台账规模、收发计数与最后一次失败原因。只读，不改任何东西")]
    public async Task BridgeState()
    {
        List<string> lines = [];
        if (api.HasCredentials == false)
        {
            lines.Add("[网关] 没启动：设置里 AppID / AppSecret 还没填，本插件什么都连不上");
            lines.Add("去 QQ 开放平台（bot.qq.com）的应用详情页把 AppID 和 AppSecret 抄进设置，填完重载插件");
            interactor.Poke(Truncate(string.Join("\n", lines)));
            return;
        }

        string? token = await api.GetAccessTokenAsync(DestroyCancellationToken);
        lines.Add("[鉴权] " + (token == null
            ? "取不到 access_token：AppID/AppSecret 对不上，或这台机器到 api.bot.qq.com 不通"
            : "能取到（密钥不外泄），" + api.TokenState));
        QqBotGatewaySystem? connection = gateway;
        lines.Add("[网关] " + (connection == null ? "没启动" : connection.StateText));
        lines.Add("[订阅] " + DescribeIntents());
        if (connection?.StoppedByPlatform == true)
        {
            lines.Add("被平台拒绝时先怀疑订阅位：把没开通的事件开关关掉再重载，4014 就是「这个 intent 你没权限」");
        }
        lock (stateGate)
        {
            lines.Add("[机器人] openid " + (botId.Length > 0 ? botId + (botIdGuessed ? "（从消息里反推的，还没在成员表里对上）" : "") : "还不知道")
                + "，收到 @ 或同步过一次群成员就会补上");
            lines.Add("[改写] " + DescribeRewriteFlags());
            lines.Add("[通讯录] " + directory.PersonCount + " 个人、" + directory.GroupCount + " 个群，"
                + (directory.LastSyncTime == null ? "还没整体同步过群成员表" : "上次同步 " + directory.LastSyncTime!.Value.ToString("MM-dd HH:mm"))
                + (Configuration.PersistDirectory ? "，落盘 " + directoryFilePath : "，只在内存里"));
            lines.Add("[菜单台账] " + ledger.Count + " 个活动" + (Configuration.PersistMenu ? "，落盘 " + menuFilePath : ""));
            lines.Add("[收发] 收进对话 " + inboundCount + " 条，发出 " + sentCount + " 条"
                + (lastSendFailure.Length > 0 ? "，最后一次发送失败：" + lastSendFailure : ""));
            lines.Add(DescribeKnownGroups());
        }
        if (connection?.IsConnected == false)
        {
            lines.Add("连不上的话看上面 [鉴权] 和 [订阅] 两行：token 取不到是凭据问题，被 4014 关掉是权限问题");
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "group_status")]
    [Description("看一个群现在的状态：群名、人数、群公告、机器人自己在群里的身份、能不能主动发消息、是不是开了全群禁言。只读，不改任何东西。这几条接口在官方那边是内邀/白名单能力，没开通会原话报权限错误")]
    public async Task GroupStatus(
        [Description("群 openid（不是 QQ 号，就是消息头 [群聊消息(...)] 里那个串）")] string groupId)
    {
        string group = RequireId(groupId, "group_status 的 groupId");
        List<string> lines = [];

        QqBotApiSystem.Result info = await groupSystem.GroupInfoAsync(group, DestroyCancellationToken);
        if (info.Data != null)
        {
            JsonElement data = info.Data.Value;
            lines.Add("[" + group + "] " + OrUnknown(FirstText(data, "group_name")));
            AppendIfPresent(lines, data, "group_member_num", "人数");
            AppendIfPresent(lines, data, "group_class_text", "群分类");
            AppendIfPresent(lines, data, "group_finger_memo", "群公告");
            AppendTags(lines, data);
            lock (stateGate)
            {
                directory.LearnGroup(group, FirstText(data, "group_name"));
            }
        }
        else
        {
            lines.Add("[" + group + "] 群名查不到：" + info.Failure);
        }

        QqBotApiSystem.Result state = await groupSystem.BotStateAsync(group, DestroyCancellationToken);
        if (state.Data != null)
        {
            JsonElement data = state.Data.Value;
            string role = FirstText(data, "member_role");
            lines.Add("机器人自己在群里的身份：" + RoleText(role));
            AppendSwitch(lines, data, "allow_proactive_msg", "平台允许机器人主动发消息");
            string receiveMode = FirstText(data, "recv_msg_setting");
            if (receiveMode.Length > 0)
            {
                lines.Add("收消息范围：" + ReceiveModeText(receiveMode) + "（这个开关不在插件里，在开放平台后台，全量群消息要靠它）");
            }
            AppendIfPresent(lines, data, "joined_at", "机器人入群时间");
            lock (stateGate)
            {
                if (botId.Length > 0 && role.Length > 0)
                {
                    directory.LearnRole(group, botId, RoleText(role));
                }
            }
        }
        else
        {
            lines.Add("机器人自己在群里的状态查不到：" + state.Failure);
        }

        QqBotApiSystem.Result mute = await groupSystem.MuteSettingAsync(group, DestroyCancellationToken);
        if (mute.Data != null)
        {
            lines.Add("全群禁言：" + GlobalMuteText(mute.Data.Value));
        }
        else
        {
            lines.Add("全群禁言查不出来：" + mute.Failure);
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "muted_members")]
    [Description("列出这个群里正在被禁言的人和解禁时间，顺带说清是不是全群禁言。官方只有一条 restrict_chat_setting 能读，接口在权限名单里，没开通会直接报错")]
    public async Task MutedMembers(
        [Description("群 openid")] string groupId)
    {
        string group = RequireId(groupId, "muted_members 的 groupId");
        QqBotApiSystem.Result setting = await groupSystem.MuteSettingAsync(group, DestroyCancellationToken);
        if (setting.Data == null)
        {
            throw new Exception("禁言名单查不到：" + setting.Failure
                + "〔这条接口（GET /v2/groups/{openid}/restrict_chat_setting）官方标了权限门槛，err_code=11253 就是应用还没拿到内邀，插件变不出权限〕");
        }
        JsonElement data = setting.Data.Value;
        List<(string MemberOpenId, string UserName, DateTime ExpireAt)> muted = QqBotGroupSystem.ReadMuted(data);
        List<string> lines = new()
        {
            "[" + group + " 全群禁言：" + GlobalMuteText(data) + "]",
        };
        if (muted.Count == 0)
        {
            lines.Add("现在没有单独被禁言的成员");
            interactor.Poke(Truncate(string.Join("\n", lines)));
            return;
        }

        int limit = Math.Clamp(Configuration.MaxMemberRows, 1, 500);
        lines.Add("禁言中 " + muted.Count + " 人，最多显示 " + limit + " 人：");
        lock (stateGate)
        {
            foreach ((string memberOpenId, string userName, DateTime expireAt) in muted.GetRange(0, Math.Min(limit, muted.Count)))
            {
                directory.Learn(memberOpenId, userName);
                lines.Add(memberOpenId + "｜" + OrUnknown(directory.DisplayName(memberOpenId))
                    + "｜" + expireAt.ToString("MM-dd HH:mm") + " 解禁（还剩 " + Remaining(expireAt) + "）");
            }
        }
        if (muted.Count > limit)
        {
            lines.Add("还有 " + (muted.Count - limit) + " 人没显示，提高「输出·成员最多列出」或点名查 find_member");
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "find_member")]
    [Description("按名字在本地通讯录里找 openid，不联网、秒回。openid 也能按中间一段搜。官方没有「按 openid 查用户资料」的接口，所以通讯录里没记录就真的没有，先 sync_group_members 或等他自己说句话")]
    public async Task FindMember(
        [Description("要查的名字，或 openid 的片段")] string keyword,
        [Description("最多列几条，默认按配置")] int limit = 0)
    {
        string text = keyword.Trim();
        if (text.Length == 0)
        {
            throw new Exception("find_member 需要给 keyword，名字或 openid 片段都行");
        }
        int shownLimit = limit > 0 ? Math.Clamp(limit, 1, 200) : Math.Clamp(Configuration.MaxMemberRows, 1, 500);
        List<PersonDirectory.MatchedPerson> matched;
        int people;
        string self;
        lock (stateGate)
        {
            matched = directory.Search(text, shownLimit);
            people = directory.PersonCount;
            self = botId;
        }
        if (matched.Count == 0)
        {
            interactor.Poke("通讯录里没有名字含「" + text + "」的人（现在记了 " + people + " 个）。"
                + "机器人的群资料接口在权限名单里，拉不到成员表就只能等对方在群里说话");
            return;
        }

        List<string> lines = new() { "[" + text + " 命中 " + matched.Count + " 人]" };
        lock (stateGate)
        {
            foreach (PersonDirectory.MatchedPerson person in matched)
            {
                lines.Add(person.UserId + "｜" + person.Name + "｜" + person.Source + "｜" + directory.Describe(person.UserId, "")
                    + (person.UserId == self && self.Length > 0 ? "｜这是机器人自己" : ""));
            }
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "sync_group_members")]
    [Description("翻完一个群的成员表灌进本地通讯录（openid、入群昵称、群内身份），之后 find_member 和消息里的昵称补全都靠它，机器人自己的 openid 也在这一步对上。官方每页只给 30 人，人多要翻很多页；这条接口在内邀/白名单里，没开通直接报错")]
    public async Task SyncGroupMembers(
        [Description("群 openid")] string groupId,
        [Description("顺便列出的成员行数，0 表示只回统计不列人")] int maxRows = 0)
    {
        string group = RequireId(groupId, "sync_group_members 的 groupId");
        (List<QqBotGroupMember> members, string failure) = await groupSystem.FetchAllMembersAsync(group, DestroyCancellationToken);
        if (members.Count == 0)
        {
            throw new Exception("成员表一条都没拿到：" + (failure.Length > 0 ? failure : "官方返回了空列表")
                + "〔GET /v2/groups/{openid}/members 官方标着「内邀接入中」，err_code=11253 就是还没给这个应用开通〕");
        }

        int rowLimit = Math.Clamp(maxRows, 0, Math.Clamp(Configuration.MaxMemberRows, 1, 500));
        int discoveredBotCount = 0;
        string discoveredBotId = "";
        List<string> rows = [];
        int totalPeople;
        lock (stateGate)
        {
            foreach (QqBotGroupMember member in members)
            {
                directory.Learn(member.OpenId, member.UserName);
                directory.LearnRole(group, member.OpenId, RoleText(member.MemberRole));
                if (member.IsBot == false)
                {
                    continue;
                }
                discoveredBotCount++;
                if (discoveredBotId.Length == 0)
                {
                    discoveredBotId = member.OpenId;
                }
            }
            if (discoveredBotCount == 1)
            {
                botId = discoveredBotId;
                botIdGuessed = false;
                rewriter.BotId = botId;
            }
            directory.MarkSynced();
            totalPeople = directory.PersonCount;
            if (Configuration.PersistDirectory)
            {
                directory.Save(directoryFilePath);
            }
            for (int index = 0; index < Math.Min(rowLimit, members.Count); index++)
            {
                QqBotGroupMember member = members[index];
                if (member.IsBot)
                {
                    continue;
                }
                rows.Add(member.OpenId + "｜" + OrUnknown(member.UserName) + "｜" + RoleText(member.MemberRole)
                    + (member.JoinedAt.Length > 0 ? "｜入群 " + member.JoinedAt : ""));
            }
        }

        List<string> lines = new()
        {
            "已同步 " + group + " 的成员表：" + members.Count + " 人，通讯录累计 " + totalPeople + " 人",
        };
        if (discoveredBotCount == 1)
        {
            lines.Add("机器人自己的 openid：" + discoveredBotId);
        }
        else if (discoveredBotCount > 1)
        {
            lines.Add("这个群里有 " + discoveredBotCount + " 个机器人，认不出哪个是你，openid 先不写进通讯录");
        }
        if (failure.Length > 0)
        {
            lines.Add("成员表没翻完：" + failure);
        }
        lines.AddRange(rows);
        if (members.Count > rowLimit)
        {
            lines.Add("只列了前 " + rowLimit + " 行，找具体的人用 find_member，别靠这里刷屏");
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "read_attached_file")]
    [Description("读聊天里收到的文件正文（txt/md/代码/配置等），图片视频这类二进制只回名字、大小和类型。地址直接用消息里 [文件: 名字 大小] 后面那个链接")]
    public async Task ReadAttachedFile(
        [Description("下载地址，优先用消息里已经给出来的那个")] string url,
        [Description("文件名，给了会用来判断是不是文本文件")] string fileName = "")
    {
        string link = RequireHttpUrl(url, "read_attached_file 的 url");
        string name = fileName.Trim();
        int byteLimit = Math.Clamp(Configuration.MaxFileBytes, 1024, 20 * 1024 * 1024);
        FetchResult fetched = await FetchWithAuthFallbackAsync(link, byteLimit);
        string suffix = GetSuffix(name.Length > 0 ? name : fetched.FileName);
        if (IsBinary(suffix, fetched.Body))
        {
            interactor.Poke("“" + OrUnknown(name) + "”不是文本文件（" + HumanSize(fetched.Length)
                + (suffix.Length > 0 ? "，类型 " + suffix : "")
                + "），只报基本信息，不读正文。图片视频要看内容只能自己认链接");
            return;
        }

        DecodedText decoded = Decode(fetched.Body);
        if (decoded.IsText == false)
        {
            string encodingHint = TryGetEncoding(936) == null ? "，这台机器还不支持 GBK（代码页 936）" : "";
            interactor.Poke("“" + OrUnknown(name) + "”（" + HumanSize(fetched.Length)
                + "）用 UTF-8、GBK 都没解出正文，按二进制处理了" + encodingHint);
            return;
        }

        int charLimit = Math.Clamp(Configuration.MaxFileChars, 200, 50000);
        string content = decoded.Content;
        string tail = content.Length > charLimit
            ? content[..charLimit] + "\n…（内容过长，已截断，剩下 " + (content.Length - charLimit) + " 字符没显示）"
            : content;
        string note = fetched.Truncated ? "（文件超过 " + HumanSize(byteLimit) + "，只下载了前面一部分）" : "";
        interactor.Poke(Truncate("“" + OrUnknown(name) + "”的内容：\n" + tail + note, charLimit + 600));
    }

    [XmlFunction(FunctionMode.Content, "send_rich")]
    [Description("发一条排版好的 QQ 消息：开闭标签之间放正文，空行分段，超过单条上限自动拆成多条按顺序发。正文里写 @名字 会自动换成真@（按通讯录唯一命中才换，重名就不换），写 <@openid> 直接就是真@。官方不支持@全体。图片/语音/文件用 send_media")]
    public async Task SendRich(
        XmlExecutorContext context,
        [Description("group 发到群 / private 发到私聊")] string type,
        [Description("群 openid 或对方的 openid")] string targetId,
        [Description("把 **加粗**、# 标题、[文字](链接) 这类 Markdown 语法换成 QQ 能正常显示的样子，默认换")] bool stripMarkdown = true)
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        bool isGroup = ParseTarget(type, "send_rich", targetId, out string target);
        string body = context.FullContent.Trim();
        if (body.Length == 0)
        {
            throw new Exception("send_rich 收到空正文，没有发出任何消息");
        }
        if (stripMarkdown)
        {
            body = Plainize(body);
        }
        List<string> segments = SplitSegments(body);
        if (segments.Count == 0)
        {
            throw new Exception("send_rich 的正文清洗完是空的，检查一下是不是整段都是图片标记");
        }

        int sent = 0;
        string route = "";
        foreach (string segment in segments)
        {
            string text;
            lock (stateGate)
            {
                text = ResolveAtNames(segment);
            }
            QqBotSendReport report = await SendTextAsync(target, isGroup, text);
            sent++;
            route = report.Route;
            if (sent < segments.Count)
            {
                await Task.Delay(SegmentGapMilliseconds, DestroyCancellationToken);
            }
        }
        interactor.Poke("已发出 " + sent + " 条" + (isGroup ? "群消息 " : "私聊消息 ") + target
            + "（正文 " + body.Length + " 字符，单条上限 " + SegmentCharLimit + "，走的是" + route + "）");
    }

    [XmlFunction(FunctionMode.OneShot, "send_media")]
    [Description("发一个图片/视频/语音/文件到 QQ：插件把消息里那个下载地址取回来、上传到官方接口、再发出去。语音只收 silk/mp3/wav/ogg，m4a/amr/aac/flac 得按 file 发。上传和发送都吃平台的主动消息频次")]
    public async Task SendMedia(
        [Description("group 发到群 / private 发到私聊")] string type,
        [Description("群 openid 或对方的 openid")] string targetId,
        [Description("image 图片 / video 视频 / voice 语音 / file 文件")] string mediaType,
        [Description("文件地址，用消息里 [图片: 链接] 或 [文件: 名字] 后面那个")] string url,
        [Description("文件名，留空时用地址里最后一段")] string fileName = "")
    {
        bool isGroup = ParseTarget(type, "send_media", targetId, out string target);
        string kind = RequireOneOf(mediaType, "send_media 的 mediaType", "image", "video", "voice", "file");
        int fileType = kind switch
        {
            "image" => MediaFileTypeImage,
            "video" => MediaFileTypeVideo,
            "voice" => MediaFileTypeVoice,
            _ => MediaFileTypeFile,
        };
        string link = RequireHttpUrl(url, "send_media 的 url");
        string name = fileName.Trim().Length > 0 ? fileName.Trim() : GetFileNameFromUrl(link);
        string suffix = GetSuffix(name);
        if (fileType == MediaFileTypeVoice && Array.IndexOf(VoiceSuffixes, suffix) < 0)
        {
            throw new Exception("语音只收 silk/mp3/wav/ogg，这个是「" + (suffix.Length > 0 ? suffix : "看不出扩展名")
                + "」；平台会把它当文件发，改用 mediaType=file");
        }

        FetchResult fetched = await FetchWithAuthFallbackAsync(link, MaxMediaBytes);
        if (fetched.Truncated)
        {
            throw new Exception("这个文件超过 " + HumanSize(MaxMediaBytes) + "，官方上传这条路发不出去（体积超限报 850031），别硬发一半");
        }

        QqBotApiSystem.Result upload = await messages.UploadAsync(
            target, isGroup, fileType, name, Convert.ToBase64String(fetched.Body), DestroyCancellationToken);
        if (upload.Data == null)
        {
            throw new Exception("上传失败：" + upload.Failure
                + "〔官方文档把 base64 直传（file_data）标成「暂未支持」、只写 url 路线，本地一直用的是 base64；如果反复卡在这一步就是踩在这个差异上〕");
        }
        string fileInfo = FirstText(upload.Data.Value, "file_info");
        if (fileInfo.Length == 0)
        {
            throw new Exception("上传成功了但响应里没有 file_info，没法发送：" + Truncate(upload.Data.Value.GetRawText(), 300));
        }

        QqBotSendReport report = await SendMediaAsync(target, isGroup, fileInfo);
        long ttl = QqBotJson.Number(upload.Data.Value, 0, "ttl");
        interactor.Poke("已发出 " + kind + "（" + name + "，" + HumanSize(fetched.Length) + "）到 "
            + (isGroup ? "群 " : "私聊 ") + target + "，走的是" + report.Route
            + (ttl > 0 ? "；这份上传的文件句柄还能复用 " + ttl + " 秒" : "；这个文件句柄长期有效"));
    }

    [XmlFunction(FunctionMode.Content, "create_menu")]
    [Description("发一个带编号的选项菜单并登记进台账。开闭标签之间一行一个选项（可以自带 1. 前缀，插件会重排）。buttons=true 时发官方的 markdown+按钮消息（要点「互动事件」权限，且 markdown 消息要平台侧配好模板），被拒或超 25 个选项会自动退回纯文本编号菜单；不管发成哪种，对方回编号都能对上")]
    public async Task CreateMenu(
        XmlExecutorContext context,
        [Description("group 发到群 / private 发到私聊")] string type,
        [Description("群 openid 或对方的 openid")] string targetId,
        [Description("菜单标题，40 字以内")] string title,
        [Description("从发出算，多少秒后截止；0 表示不设截止时间")] int ttlSeconds = 0,
        [Description("截止时间，写 21:30 这种当天时刻（过了就顺延到次日）、或 2026-10-20 18:00、或 Unix 秒；和 ttl 二选一")] string until = "",
        [Description("总共收多少个选择，0 表示不限")] int max = 0,
        [Description("每个人最多选几次，0 表示不限")] int per = 0,
        [Description("每人只让选一次")] bool once = false,
        [Description("同一个人选同一项之后多少秒内不再算，0 表示不限")] int cooldownSeconds = 0,
        [Description("超限或过期后是否在收到的消息里写明「这条不要执行」，关掉就只提示不拦")] bool hard = true,
        [Description("所有选项共用一个名额（默认每个选项各记各的）")] bool shared = false,
        [Description("名额用满时在对账里额外提醒一句")] bool notify = false,
        [Description("true 就发成官方带按钮的消息（只有 markdown 消息能挂按钮，且要互动事件权限）；选项超过 25 个会自动退回纯文本")] bool buttons = false)
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        bool isGroup = ParseTarget(type, "create_menu", targetId, out string target);
        string cleanTitle = title.Trim();
        if (cleanTitle.Length == 0)
        {
            throw new Exception("create_menu 缺 title");
        }
        List<string> options = ParseOptions(context.FullContent);
        if (options.Count < 2)
        {
            throw new Exception("create_menu 的选项至少两项，开闭标签之间一行一个");
        }
        int bodyLength = cleanTitle.Length + 60;
        foreach (string option in options)
        {
            bodyLength += option.Length + 4;
        }
        if (bodyLength > SegmentCharLimit)
        {
            throw new Exception("这个菜单光标题和选项就有 " + bodyLength + " 字符，超过单条上限 " + SegmentCharLimit
                + "，精简选项或拆成两个菜单");
        }

        MenuLedger.MenuRecord record;
        string text;
        lock (stateGate)
        {
            record = ledger.Create(
                isGroup ? target : "", isGroup ? "" : target, cleanTitle, options,
                Math.Clamp(ttlSeconds, 0, 30 * 24 * 3600), until,
                Math.Max(max, 0), Math.Max(per, 0), once, Math.Max(cooldownSeconds, 0),
                hard, shared, notify);
            text = BuildMenuText(record);
        }

        string? anchor = ResolveAnchor(isGroup, target);
        string rejection = "";
        QqBotSendReport report;
        if (buttons == false || record.Options.Count > MaxKeyboardRows * MaxButtonsPerRow)
        {
            report = await messages.SendTextAsync(target, isGroup, text, anchor, DestroyCancellationToken);
            if (buttons)
            {
                rejection = "选项有 " + record.Options.Count + " 个，超过官方键盘上限 " + MaxKeyboardRows * MaxButtonsPerRow;
            }
        }
        else
        {
            report = await messages.SendMarkdownAsync(
                target, isGroup, text, BuildKeyboard(record), anchor, DestroyCancellationToken);
            if (report.Ok == false)
            {
                rejection = report.Failure;
                report = await messages.SendTextAsync(target, isGroup, text, anchor, DestroyCancellationToken);
            }
        }
        if (report.Ok == false)
        {
            RememberSendFailure(report);
            throw new Exception("菜单没发出去：" + report.Failure);
        }

        record.MessageId = report.MessageId.Length > 0 ? report.MessageId : null;
        string row;
        lock (stateGate)
        {
            if (Configuration.PersistMenu)
            {
                ledger.Save(menuFilePath);
            }
            sentCount++;
            row = MenuLedger.FormatRow(record);
        }
        interactor.Poke(row + "｜已发到 " + (isGroup ? "群 " : "私聊 ") + target + "（" + report.Route + "）"
            + (rejection.Length > 0 ? "｜按钮没发出去，退回纯文本：" + rejection + "，让对方直接回编号" : "")
            + "｜短码 " + record.Code + "，对方回 1、2、3 就会对上这条；要收摊用 menu_manage action=close selector=" + record.Seq);
    }

    [XmlFunction(FunctionMode.OneShot, "menu_manage")]
    [Description("管台账里的编号菜单：看统计、提前关闭、清空重选、延长截止时间。只动本地台账，不会重发消息")]
    public async Task MenuManage(
        [Description("stats 看台账 / close 关闭 / reset 清空重选 / extend 延长")] string action,
        [Description("哪个菜单：标题、#序号 或短码（~开头）。stats 时可以留空表示全部，或填关键词筛")] string selector = "",
        [Description("extend 延长多少分钟，至少 1")] int minutes = 0,
        [Description("close 的理由，写给自己看")] string reason = "",
        [Description("stats 最多列几个")] int limit = 0)
    {
        string resolvedAction = RequireOneOf(action, "menu_manage 的 action", "stats", "close", "reset", "extend");
        if (resolvedAction == "stats")
        {
            int shownLimit = limit > 0 ? Math.Clamp(limit, 1, 50) : 20;
            string stats;
            lock (stateGate)
            {
                stats = ledger.FormatStats(selector, shownLimit);
            }
            interactor.Poke(Truncate(stats));
            return;
        }

        string wanted = selector.Trim();
        if (wanted.Length == 0)
        {
            throw new Exception("menu_manage 的 " + resolvedAction + " 要指定 selector（标题、#序号 或短码）");
        }
        if (resolvedAction == "extend" && minutes < 1)
        {
            throw new Exception("menu_manage action=extend 要给 minutes，至少 1 分钟");
        }
        bool changed;
        string row;
        lock (stateGate)
        {
            changed = resolvedAction switch
            {
                "close" => ledger.Close(wanted, "manual"),
                "reset" => ledger.Reset(wanted),
                _ => ledger.Extend(wanted, minutes),
            };
            if (changed && Configuration.PersistMenu)
            {
                ledger.Save(menuFilePath);
            }
            MenuLedger.MenuRecord? record = changed ? ledger.Find(wanted) : null;
            row = record == null ? wanted : MenuLedger.FormatRow(record);
        }
        if (changed == false)
        {
            interactor.Poke("台账里找不到「" + wanted + "」这个菜单，或者它已经关了。先用 menu_manage action=stats 看看有哪些");
            return;
        }
        interactor.Poke(resolvedAction switch
        {
            "close" => "已关闭 " + row + (reason.Trim().Length > 0 ? "｜理由：" + reason.Trim() : ""),
            "reset" => "已清空重选 " + row,
            _ => "已延长 " + minutes + " 分钟：" + row,
        });
    }

    // ============================================================
    // 网关事件
    // ============================================================

    void StartGateway()
    {
        if (api.HasCredentials == false)
        {
            logger.LogInformation("[QQ增强] 没配 AppID/AppSecret，官方机器人这条路不启动");
            return;
        }
        long intents = IntentGroupAndC2cMessages;
        if (Configuration.SubscribeMemberEvents)
        {
            intents |= IntentGroupMemberEvents;
        }
        if (Configuration.SubscribeInteractions)
        {
            intents |= IntentInteractionEvents;
        }
        if (Configuration.SubscribeMessageAudit)
        {
            intents |= IntentMessageAuditEvents;
        }

        QqBotGatewaySystem connection = new(api, intents);
        connection.EventReceived += OnGatewayEvent;
        connection.ConnectionFailed += OnConnectionFailed;
        gateway = connection;
        connection.Start();
    }

    void OnConnectionFailed(string reason)
    {
        lock (stateGate)
        {
            if (reason == lastGatewayFailure)
            {
                return;
            }
            lastGatewayFailure = reason;
        }
        logger.LogWarning("[QQ增强] 官方机器人网关：{Reason}", reason);
    }

    // 事件的回调跑在 WebSocket 接收线程上，一条 AI 回合要几十秒，绝不能在这里 await：
    // 只把任务挂到链条尾巴上，接收线程立刻返回，处理顺序仍然按到达顺序
    void OnGatewayEvent(QqBotGatewaySystem.Event incoming)
    {
        lock (eventChainGate)
        {
            eventChain = ChainEventAsync(eventChain, incoming);
        }
    }

    async Task ChainEventAsync(Task previous, QqBotGatewaySystem.Event incoming)
    {
        await previous;
        await HandleEventAsync(incoming);
    }

    async Task HandleEventAsync(QqBotGatewaySystem.Event incoming)
    {
        try
        {
            if (incoming.Type.EndsWith("_MESSAGE_CREATE", StringComparison.Ordinal))
            {
                QqBotInbound? inbound = QqBotInbound.TryParse(incoming.Type, incoming.Data);
                if (inbound != null)
                {
                    await HandleMessageAsync(inbound);
                    return;
                }
                logger.LogWarning("[QQ增强] 收到一条 {Type}，但连发言人 openid 都读不出来，原文：{Raw}",
                    incoming.Type, Truncate(incoming.Data.GetRawText(), 300));
                return;
            }
            if (incoming.Type == "INTERACTION_CREATE")
            {
                await HandleInteractionAsync(incoming.Data);
                return;
            }
            if (incoming.Type == "READY" || incoming.Type == "RESUMED")
            {
                return;
            }
            interactor.Poke(Truncate(DescribeOtherEvent(incoming.Type, incoming.Data)));
        }
        catch (OperationCanceledException) when (DestroyCancellationToken.IsCancellationRequested)
        {
            // 插件重载时打断正在跑的模型回合，这是正常收尾
        }
        catch (Exception eventError)
        {
            logger.LogWarning(eventError, "[QQ增强] 处理事件 {Type} 失败，这条不要影响后面的事件", incoming.Type);
            interactor.Poke("[QQ增强] 处理一条 " + incoming.Type + " 事件时出错：" + Truncate(eventError.Message, 300));
        }
    }

    async Task HandleMessageAsync(QqBotInbound inbound)
    {
        if (IsDuplicateMessage(inbound.MessageId))
        {
            return;
        }
        LearnFromInbound(inbound);
        RememberAnchor(inbound);

        string text = Truncate(RewriteInbound(inbound.ToConversationText()) + "\n" + ReplyReminder);
        // 单聊和点名@机器人的是要机器人答话，走真回合；群里没@的闲聊只进缓冲区，不然每句都点一次模型
        if (inbound.IsGroup == false || inbound.EventType == "GROUP_AT_MESSAGE_CREATE")
        {
            await interactor.ChatAsync(text);
            return;
        }
        interactor.Poke(text);
    }

    async Task HandleInteractionAsync(JsonElement data)
    {
        string interactionId = QqBotJson.Text(data, "id");
        int kind = QqBotJson.Integer(data, 0, "type");
        // 只有 11（消息按钮）和 12（单聊快捷菜单）要应答，不应答对方客户端会一直转圈到超时
        if (kind == 11 || kind == 12)
        {
            if (interactionId.Length == 0)
            {
                logger.LogWarning("[QQ增强] 收到一条要应答的互动事件，但体里没有 id");
            }
            else
            {
                QqBotApiSystem.Result ack = await messages.AckInteractionAsync(interactionId, 0, DestroyCancellationToken);
                if (ack.Data == null)
                {
                    logger.LogWarning("[QQ增强] 互动应答失败（{Id}）：{Failure}", interactionId, ack.Failure);
                }
            }
        }

        JsonElement resolved = QqBotJson.Child(QqBotJson.Child(data, "data"), "resolved");
        string buttonData = QqBotJson.Text(resolved, "button_data");
        string buttonId = QqBotJson.Text(resolved, "button_id");
        string userOpenId = FirstNonEmpty(
            FirstNonEmpty(QqBotJson.Text(data, "user_openid"), QqBotJson.Text(data, "group_member_openid")),
            QqBotJson.Text(resolved, "user_id"));
        string groupId = QqBotJson.Text(data, "group_openid");
        string name;
        string verdict;
        lock (stateGate)
        {
            name = OrUnknown(directory.DisplayName(userOpenId));
            // 按钮的 data 是插件自己写的「编号 短码」，直接走同一套台账判定，编号和权限规则一起生效
            MenuLedger.MatchResult? match = Configuration.BindMenuReplies
                ? ledger.TryMatch(userOpenId, groupId, buttonData)
                : null;
            verdict = match == null ? "" : match.Verdict;
        }
        if (verdict.Length == 0)
        {
            interactor.Poke("[按钮] " + name + "（" + (userOpenId.Length > 0 ? userOpenId : "没带 openid") + "）点了按钮 "
                + (buttonData.Length > 0 ? "「" + Truncate(buttonData, 60) + "」" : buttonId)
                + (groupId.Length > 0 ? "，群 " + groupId : "") + "｜没对上台账里的菜单");
            return;
        }
        interactor.Poke(Truncate("[按钮] " + name + " 点了选项按钮｜" + verdict));
    }

    string DescribeOtherEvent(string type, JsonElement data)
    {
        string group = QqBotJson.Text(data, "group_openid");
        string groupLabel = group.Length > 0
            ? group + "（" + OrUnknown(GroupLabelOf(group)) + "）"
            : "没带群号";
        string operatorId = FirstNonEmpty(QqBotJson.Text(data, "op_member_openid"), QqBotJson.Text(data, "member_openid"));
        return type switch
        {
            "GROUP_ADD_ROBOT" => "[QQ事件] 机器人被添加进群 " + groupLabel + "，操作人 " + WhoIs(operatorId),
            "GROUP_DEL_ROBOT" => "[QQ事件] 机器人被移出群 " + groupLabel + "，操作人 " + WhoIs(operatorId),
            "GROUP_MSG_RECEIVE" => "[QQ事件] 群 " + groupLabel + " 重新允许机器人收全量消息了",
            "GROUP_MSG_REJECT" => "[QQ事件] 群 " + groupLabel + " 关闭了机器人收全量消息，之后只有被@才进得来",
            "C2C_MSG_RECEIVE" => "[QQ事件] 单聊 " + WhoIs(operatorId) + " 重新接收机器人消息了",
            "C2C_MSG_REJECT" => "[QQ事件] 单聊 " + WhoIs(operatorId) + " 拒收机器人消息了，先发出去也到不了",
            "GROUP_MEMBER_ADD" => "[QQ事件] " + WhoIs(operatorId) + " 进了群 " + groupLabel,
            "GROUP_MEMBER_REMOVE" => "[QQ事件] " + WhoIs(operatorId) + " 退出了群 " + groupLabel,
            "GROUP_JOIN_REQUEST" => DescribeJoinRequest(data, groupLabel),
            "FRIEND_ADD" => "[QQ事件] " + WhoIs(operatorId) + " 加机器人为好友",
            "FRIEND_DEL" => "[QQ事件] " + WhoIs(operatorId) + " 解除了和机器人的好友关系",
            "MESSAGE_AUDIT_PASS" => "[QQ事件] 一条消息过了审核",
            "MESSAGE_AUDIT_REJECT" => "[QQ事件] 一条消息被审核拦下了：官方返回 " + Truncate(data.GetRawText(), 200),
            _ => "[QQ事件] 收到一个本插件还不认识的事件 " + type + "：" + Truncate(data.GetRawText(), 200),
        };
    }

    string DescribeJoinRequest(JsonElement data, string groupLabel)
    {
        string applicant = FirstNonEmpty(QqBotJson.Text(data, "member_openid"), QqBotJson.Text(data, "union_openid"));
        return "[QQ事件] 有人申请加群 " + groupLabel + "：" + OrUnknown(QqBotJson.Text(data, "username"))
            + "（" + applicant + "）｜验证消息 " + OrUnknown(QqBotJson.Text(data, "verify_info"))
            + "｜来源 " + OrUnknown(QqBotJson.Text(data, "apply_source"))
            + "｜审批动作在「群管」插件那边，本插件只负责把这件事报进来";
    }

    string WhoIs(string openId)
    {
        if (openId.Length == 0)
        {
            return "不知道是谁";
        }
        lock (stateGate)
        {
            return openId + "（" + OrUnknown(directory.DisplayName(openId)) + "）";
        }
    }

    void LearnFromInbound(QqBotInbound inbound)
    {
        lock (stateGate)
        {
            directory.Learn(inbound.UserId, inbound.UserName);
            if (inbound.IsGroup && inbound.MemberRole.Length > 0)
            {
                directory.LearnRole(inbound.GroupId, inbound.UserId, RoleText(inbound.MemberRole));
            }
            foreach (QqBotMention mention in inbound.Mentions)
            {
                directory.Learn(mention.OpenId, mention.UserName);
                if (inbound.IsGroup)
                {
                    directory.LearnRole(inbound.GroupId, mention.OpenId, RoleText(mention.MemberRole));
                }
            }
            inboundCount++;
        }
        LearnSelfOpenId(inbound);
    }

    // 官方不把自己写进 mentions，所以「@ 里有、名单里没有」的那个就是机器人自己；这条比 author.bot 可靠
    void LearnSelfOpenId(QqBotInbound inbound)
    {
        string candidate = "";
        bool confirmed = false;
        foreach (string atId in inbound.AtIds)
        {
            bool listed = inbound.Mentions.Exists(mention => mention.OpenId == atId);
            if (listed == false)
            {
                candidate = atId;
                confirmed = true;
                break;
            }
        }
        if (candidate.Length == 0 && inbound.AuthorIsBot)
        {
            candidate = inbound.UserId;
        }
        if (candidate.Length == 0)
        {
            return;
        }
        lock (stateGate)
        {
            if (botId.Length > 0 && (botIdGuessed == false || confirmed == false))
            {
                return;
            }
            botId = candidate;
            botIdGuessed = confirmed == false;
            rewriter.BotId = botId;
        }
    }

    bool IsDuplicateMessage(string messageId)
    {
        if (messageId.Length == 0)
        {
            return false;
        }
        lock (stateGate)
        {
            // 官方明说同一个 msg_id 可能重复推送
            if (seenMessageIds.Add(messageId) == false)
            {
                return true;
            }
            seenMessageOrder.Enqueue(messageId);
            while (seenMessageIds.Count > SeenMessageCapacity && seenMessageOrder.Count > 0)
            {
                seenMessageIds.Remove(seenMessageOrder.Dequeue());
            }
        }
        return false;
    }

    void RememberAnchor(QqBotInbound inbound)
    {
        if (inbound.MessageId.Length == 0)
        {
            return;
        }
        lock (stateGate)
        {
            if (anchorByTarget.Count > ReplyAnchorCapacity)
            {
                anchorByTarget.Clear();
            }
            anchorByTarget[inbound.ConversationId] = new Anchor(
                inbound.MessageId, ParseEventTime(inbound.Timestamp));
        }
    }

    string? ResolveAnchor(bool isGroup, string target)
    {
        lock (stateGate)
        {
            if (anchorByTarget.TryGetValue(target, out Anchor? anchor) == false)
            {
                return null;
            }
            return DateTime.Now - anchor.ReceivedAt <= (isGroup ? GroupReplyWindow : PrivateReplyWindow)
                ? anchor.MessageId
                : null;
        }
    }

    string RewriteInbound(string text)
    {
        if (Configuration.EnableRewrite == false)
        {
            return text;
        }
        rewriter.ResolveAtName = Configuration.ResolveAtName;
        rewriter.MarkSelfEcho = Configuration.MarkSelfEcho;
        rewriter.BindMenuReplies = Configuration.BindMenuReplies;
        try
        {
            lock (stateGate)
            {
                return rewriter.Rewrite(text);
            }
        }
        catch (Exception rewriteError)
        {
            // 改写坏了不能把消息一起吞掉，原文照样进对话，只报一次
            if (rewriteFailureReported == false)
            {
                rewriteFailureReported = true;
                logger.LogWarning(rewriteError, "[QQ增强] 消息改写失败，这条原文进对话（后续同类失败不再重复报）");
            }
            return text;
        }
    }

    async Task<QqBotSendReport> SendTextAsync(string target, bool isGroup, string text)
    {
        QqBotSendReport report = await messages.SendTextAsync(
            target, isGroup, text, ResolveAnchor(isGroup, target), DestroyCancellationToken);
        return FinishSend(report);
    }

    async Task<QqBotSendReport> SendMediaAsync(string target, bool isGroup, string fileInfo)
    {
        QqBotSendReport report = await messages.SendMediaAsync(
            target, isGroup, fileInfo, ResolveAnchor(isGroup, target), DestroyCancellationToken);
        return FinishSend(report);
    }

    QqBotSendReport FinishSend(QqBotSendReport report)
    {
        lock (stateGate)
        {
            if (report.Ok == false)
            {
                lastSendFailure = report.Failure;
            }
            else
            {
                sentCount++;
            }
        }
        if (report.Ok == false)
        {
            throw new Exception("消息没发出去：" + report.Failure);
        }
        return report;
    }

    void RememberSendFailure(QqBotSendReport report)
    {
        lock (stateGate)
        {
            lastSendFailure = report.Failure;
        }
    }

    // ============================================================
    // 发送侧文本处理
    // ============================================================

    string ResolveAtNames(string text)
    {
        string normalized = NormalizeAtMarkup(text);
        // 模型会把收到的 @openid 原样抄回来，这种先补成官方的 <@openid>，不然发出去是一串乱码似的字母
        string withIds = OutgoingOpenIdPattern.Replace(normalized, match => "<@" + match.Groups["id"].Value + ">");
        // 官方键盘里没有@全体，写成 @全体 只会是普通文字
        return OutgoingAtPattern.Replace(withIds, match =>
        {
            string name = match.Groups[1].Value;
            if (directory.TryResolveByName(name, out string userId))
            {
                return "<@" + userId + ">";
            }
            return match.Value;
        });
    }

    static string NormalizeAtMarkup(string text)
    {
        // 新形态 <qqbot-at-user id="…" /> 在群里被当纯文本显示，一律换成 legacy 的 <@openid>
        return OutgoingAtTagPattern.Replace(text, match => "<@" + match.Groups[1].Value + ">");
    }

    static object BuildKeyboard(MenuLedger.MenuRecord record)
    {
        List<object> rows = [];
        // 官方上限是 5 行 × 5 列（超了报 40034029），这里再兜一道，别让调用方数错
        for (int start = 0; start < record.Options.Count && rows.Count < MaxKeyboardRows; start += MaxButtonsPerRow)
        {
            List<object> buttons = [];
            int end = Math.Min(start + MaxButtonsPerRow, record.Options.Count);
            for (int index = start; index < end; index++)
            {
                string label = ButtonLabel(record.Options[index]);
                buttons.Add(new
                {
                    id = "b" + (index + 1),
                    render_data = new { label, style = 0, visited_label = "已选 " + label },
                    action = new
                    {
                        type = 1,
                        // 点回来的是这段字符串，交给同一套台账解析：编号 + 短码，能唯一对上这条菜单
                        data = (index + 1) + " " + record.Code,
                        unsupport_tips = "这个客户端点不了按钮，直接回编号 " + (index + 1) + " 就行",
                    },
                });
            }
            rows.Add(new { buttons });
        }
        return new { content = new { rows } };
    }

    static string ButtonLabel(string option)
    {
        string trimmed = option.Trim();
        if (trimmed.Length == 0)
        {
            return "（空选项）";
        }
        if (trimmed.Length <= ButtonLabelMaxLength)
        {
            return trimmed;
        }
        int cut = ButtonLabelMaxLength;
        if (char.IsHighSurrogate(trimmed[cut - 1]))
        {
            cut--;
        }
        return trimmed[..cut] + "…";
    }

    const string ReplyReminder =
        "（要回他得用 send_rich 或 send_media 发出去，直接写在这条回复里的话 QQ 那边看不见）";

    const string RewriteGuide =
        "本插件（QQ官方机器人聊天增强）走的是 QQ 开放平台的官方机器人接口，自己收自己发，和 QQ聊天（OneBot）插件没关系。"
        + "它把进来的 QQ 消息写成这样的形态："
        + "第一行 [群聊消息(群openid)] 或 [私聊消息(对方openid)] 是来源，群号是官方给的 openid 串，不是 QQ 号；"
        + "后面每行 [发言人openid(昵称)]: 正文，昵称只有见过才有，官方没有群名片、也没有按 openid 查资料的接口；"
        + "@ 后面已经换成了名字，@你 就是在叫机器人自己；带 ·你自己 的是机器人自己发出去又被推回来的那条；"
        + "[语音: 识别文字] 是平台自带的语音转写、[图片: 链接]、[视频: 链接]、[文件: 名字 大小] 链接 是聊天里的附件，链接可以直接丢给 read_attached_file 或 send_media；"
        + "〔引用 …〕是这条消息引用的原文，〔卡片 …〕是结构化卡片；"
        + "消息结尾的〔〕是编号菜单的对账结果，写了「有效」才继续按那个选项办，写了「不要执行」或「别猜」就别办，直接问清楚；"
        + "[按钮] 开头的是对方点了 create_menu 发出去的按钮，内容和回编号一样进台账判定。"
        + "要回 QQ 只能用工具发：正文用 send_rich（自动分段、写 @名字 会自动换成真@，官方不支持@全体），"
        + "图片/语音/文件用 send_media（地址用消息里那个链接），给人挑选用 create_menu（要按钮就 buttons=true，没权限会自动退回纯文本）；"
        + "编号菜单的台账用 menu_manage 看统计或收摊。"
        + "找人先 find_member（本地通讯录，不联网），没记录就 sync_group_members 拉一次成员表——这条接口和 group_status、muted_members 都在官方的内邀/白名单里，"
        + "报 err_code=11253 就是应用还没开通，代码变不出权限。"
        + "机器人自己的 openid 要靠第一条 @ 或成员表才知道，状态看 bridge_state。"
        + "禁言、踢人、公告、入群审批属于「群管」插件，本插件只提供查询和收发。";

    // ============================================================
    // 状态描述
    // ============================================================

    string DescribeRewriteFlags()
    {
        if (Configuration.EnableRewrite == false)
        {
            return "总开关是关的，收到的消息原文进对话";
        }
        List<string> enabled = [];
        if (Configuration.ResolveAtName)
        {
            enabled.Add("@名字");
        }
        if (Configuration.MarkSelfEcho)
        {
            enabled.Add("标注自己回声");
        }
        if (Configuration.BindMenuReplies)
        {
            enabled.Add("菜单对账");
        }
        return enabled.Count == 0 ? "总开关开着但子项都关了" : string.Join("、", enabled);
    }

    string DescribeIntents()
    {
        List<string> parts = new() { "1<<25 消息事件（单聊、群聊，必开）" };
        parts.Add(Configuration.SubscribeMemberEvents ? "1<<24 成员进出（已订阅）" : "1<<24 成员进出（没订）");
        parts.Add(Configuration.SubscribeInteractions ? "1<<26 按钮点击（已订阅）" : "1<<26 按钮点击（没订，按钮消息收不到点击）");
        parts.Add(Configuration.SubscribeMessageAudit ? "1<<27 消息审核（已订阅）" : "1<<27 消息审核（没订）");
        return string.Join("｜", parts);
    }

    string DescribeKnownGroups()
    {
        List<string> groups = directory.KnownGroupIds();
        if (groups.Count == 0)
        {
            return "[已知的群] 一个都还没记到。官方没有「列出机器人所在群」的接口，只有群里说过话或查过群资料才会记下来";
        }
        List<string> rows = [];
        foreach (string group in groups)
        {
            string name = directory.GroupName(group);
            rows.Add(name.Length > 0 ? group + "（" + name + "）" : group);
        }
        return "[已知的群] " + rows.Count + " 个：" + string.Join("、", rows);
    }

    string GroupLabelOf(string groupId)
    {
        lock (stateGate)
        {
            return directory.GroupName(groupId);
        }
    }

    // ============================================================
    // 输出与格式
    // ============================================================

    static string BuildMenuText(MenuLedger.MenuRecord record)
    {
        List<string> lines = new() { record.Title };
        for (int index = 0; index < record.Options.Count; index++)
        {
            lines.Add((index + 1) + ". " + record.Options[index]);
        }
        lines.Add("回复编号就行" + (record.MaxTotal > 0 ? "，总共收 " + record.MaxTotal + " 个" : "")
            + (record.PerUser > 0 ? "，每人 " + record.PerUser + " 次" : "")
            + (record.ExpiresAt != null ? "，" + record.ExpiresAt!.Value.ToString("MM月dd日 HH:mm") + " 前" : "")
            + "（短码 " + record.Code + "）");
        return string.Join("\n", lines);
    }

    static List<string> ParseOptions(string body)
    {
        List<string> options = [];
        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            line = NumberedOptionPattern.Replace(line, "");
            line = BulletedOptionPattern.Replace(line, "");
            if (line.Length > 0)
            {
                options.Add(line);
            }
        }
        return options;
    }

    static List<string> SplitSegments(string body)
    {
        List<string> segments = [];
        List<string> paragraph = [];
        int length = 0;
        foreach (string rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            foreach (string line in ChunkLongLine(rawLine))
            {
                bool isBlank = line.Trim().Length == 0;
                if (length + line.Length + 1 > SegmentCharLimit)
                {
                    if (paragraph.Count > 0)
                    {
                        segments.Add(string.Join("\n", paragraph));
                        paragraph.Clear();
                    }
                    length = 0;
                }
                if (isBlank)
                {
                    if (paragraph.Count > 0)
                    {
                        segments.Add(string.Join("\n", paragraph));
                        paragraph.Clear();
                        length = 0;
                    }
                    continue;
                }
                paragraph.Add(line);
                length += line.Length + 1;
            }
        }
        if (paragraph.Count > 0)
        {
            segments.Add(string.Join("\n", paragraph));
        }
        return segments;
    }

    // 一行就顶破上限的内容（贴长的日志、一长串链接）也得硬拆，不然这一条永远发不出去
    static List<string> ChunkLongLine(string line)
    {
        if (line.Length <= SegmentCharLimit)
        {
            return [line];
        }
        List<string> pieces = [];
        for (int start = 0; start < line.Length;)
        {
            int take = Math.Min(SegmentCharLimit, line.Length - start);
            // 末位正好是 emoji 的前半个字就挪到下一段，不然切出来是乱码
            if (start + take < line.Length && char.IsHighSurrogate(line[start + take - 1]))
            {
                take--;
            }
            pieces.Add(line.Substring(start, take));
            start += take;
        }
        return pieces;
    }

    static string Plainize(string body)
    {
        List<string> lines = [];
        foreach (string rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine;
            if (line.TrimStart().StartsWith("```"))
            {
                continue;
            }
            line = Regex.Replace(line, @"^#{1,6}\s*", "");
            line = Regex.Replace(line, @"^(\s*)[-*+]\s+\[[ xX]\]\s+", "${1}· ");
            line = Regex.Replace(line, @"^(\s*)[-*+]\s+", "${1}· ");
            line = Regex.Replace(line, @"^>\s*", "│ ");
            // 图片先摘掉，否则 ![文字](链接) 会先被下面那条链接规则改成 文字：链接 留下一串没用的图床地址
            line = Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", "");
            line = Regex.Replace(line, @"\[([^\]]+)\]\(([^)\s]+)\)", "$1：$2");
            line = Regex.Replace(line, @"\*\*([^*]+)\*\*", "$1");
            line = Regex.Replace(line, @"__([^_]+)__", "$1");
            line = Regex.Replace(line, @"`([^`]+)`", "$1");
            lines.Add(line.TrimEnd());
        }
        return string.Join("\n", lines).Trim();
    }

    static void AppendIfPresent(List<string> lines, JsonElement element, string name, string label)
    {
        string value = FirstText(element, name);
        if (value.Length > 0)
        {
            lines.Add(label + "：" + value);
        }
    }

    static void AppendSwitch(List<string> lines, JsonElement element, string name, string label)
    {
        string raw = FirstText(element, name);
        string text = raw.ToLowerInvariant() switch
        {
            "true" or "1" => "是",
            "false" or "0" => "否",
            _ => "",
        };
        if (text.Length > 0)
        {
            lines.Add(label + "：" + text);
        }
    }

    static void AppendTags(List<string> lines, JsonElement element)
    {
        List<string> tags = [];
        foreach (JsonElement item in QqBotJson.Array(element, "group_tags"))
        {
            string text = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            if (text.Length > 0)
            {
                tags.Add(text);
            }
        }
        if (tags.Count > 0)
        {
            lines.Add("群标签：" + string.Join("、", tags));
        }
    }

    static string GlobalMuteText(JsonElement payload)
    {
        JsonElement rule = QqBotJson.Child(payload, "global_rule");
        string mode = QqBotJson.Text(rule, "mode");
        if (mode.Length == 0)
        {
            return "这个接口没给全局禁言规则";
        }
        return mode switch
        {
            "none" => "没开",
            "always" => "一直禁着",
            "schedule" => "定时禁言中（到 " + OrUnknown(QqBotJson.Text(rule, "expire_at")) + "）",
            _ => mode,
        };
    }

    static string RoleText(string role) => role switch
    {
        "owner" => "群主",
        "admin" => "管理员",
        "member" => "普通成员",
        "" => "未知",
        _ => role,
    };

    static string ReceiveModeText(string mode) => mode switch
    {
        "all" => "群里每一条消息都推（要后台开「接收所有消息」）",
        "only_mention" => "只推@机器人的",
        "mention_and_context" => "推@机器人的和它前后的上下文",
        _ => mode,
    };

    static string OrUnknown(string value) => value.Length > 0 ? value : "（未知）";

    static string FirstNonEmpty(string first, string second) => first.Length > 0 ? first : second;

    static string FirstText(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            string value = FieldText(element, name);
            if (value.Length > 0)
            {
                return value;
            }
        }
        return "";
    }

    // 官方同一批字段里数字和字符串会互换，布尔也是三种形态都见过，统一读成正文再判
    static string FieldText(JsonElement element, string name)
    {
        JsonElement child = QqBotJson.Child(element, name);
        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString() ?? "",
            JsonValueKind.Number => child.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    static DateTime ParseEventTime(string text)
    {
        // 消息事件给的是 RFC3339 串，个别事件给的是 Unix 秒；两种都读，读不出就当现在
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed.LocalDateTime;
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix) && unix > 1_000_000_000)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        }
        return DateTime.Now;
    }

    static string Remaining(DateTime until)
    {
        TimeSpan left = until - DateTime.Now;
        if (left.TotalSeconds <= 0)
        {
            return "已到期";
        }
        return left.TotalHours >= 1
            ? ((int)left.TotalHours) + " 小时 " + left.Minutes + " 分"
            : left.Minutes + " 分 " + left.Seconds + " 秒";
    }

    static string RequireId(string value, string fieldName)
    {
        string text = value.Trim();
        if (text.Length == 0)
        {
            throw new Exception(fieldName + " 是空的：官方机器人只认 openid（消息里那串字母数字），不认 QQ 号");
        }
        if (OpenIdShape.IsMatch(text) == false)
        {
            throw new Exception(fieldName + "「" + Truncate(text, 80) + "」不像 openid。"
                + "群号看消息头 [群聊消息(...)]，人名先用 find_member 换 openid");
        }
        return text;
    }

    static string RequireHttpUrl(string value, string fieldName)
    {
        string text = value.Trim();
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase) == false)
        {
            throw new Exception(fieldName + " 得是 http/https 链接，收到的是「" + Truncate(text, 120) + "」");
        }
        return text;
    }

    static bool ParseTarget(string type, string toolName, string targetId, out string target)
    {
        string resolved = RequireOneOf(type, toolName + " 的 type", "group", "private");
        target = RequireId(targetId, toolName + " 的 targetId");
        return resolved == "group";
    }

    static string RequireOneOf(string value, string fieldName, params string[] allowed)
    {
        string text = value.Trim().ToLowerInvariant();
        foreach (string option in allowed)
        {
            if (text == option)
            {
                return option;
            }
        }
        throw new Exception(fieldName + " 只能是 " + string.Join(" / ", allowed) + "，收到的是「" + value + "」");
    }

    string Truncate(string text) => Truncate(text, Math.Clamp(Configuration.MaxOutputChars, 500, 20000));

    static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "\n…（超出 " + limit + " 字符，已截断）";

    // ============================================================
    // 附件下载与解码
    // ============================================================

    // 官方文档没说附件链接要不要带鉴权头，实测过才知道；被拒时带上 token 再试一次，而不是直接失败
    async Task<FetchResult> FetchWithAuthFallbackAsync(string link, int byteLimit)
    {
        try
        {
            return await FetchAsync(link, byteLimit);
        }
        catch (FetchRejectedException rejected) when (rejected.StatusCode == 401 || rejected.StatusCode == 403)
        {
            string? token = await api.GetAccessTokenAsync(DestroyCancellationToken);
            if (token == null)
            {
                throw new Exception("这个附件地址要登录态才给下载（服务器回 " + rejected.StatusCode
                    + "），但 access_token 取不到：" + api.TokenState);
            }
            return await FetchAsync(link, byteLimit, token);
        }
    }

    async Task<FetchResult> FetchAsync(string url, int byteLimit, string token = "")
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(DefaultFileFetchTimeoutSeconds));
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (token.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "QQBot " + token);
        }
        HttpResponseMessage response;
        try
        {
            response = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (DestroyCancellationToken.IsCancellationRequested == false)
        {
            throw new Exception("下载超时（" + DefaultFileFetchTimeoutSeconds + " 秒），这个地址可能已经过期或连不上");
        }
        using (response)
        {
            if (response.IsSuccessStatusCode == false)
            {
                throw new FetchRejectedException((int)response.StatusCode);
            }

            string name = GetFileNameFromUrl(url);
            long declaredLength = response.Content.Headers.ContentLength ?? -1;
            using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            bool truncated = false;
            while (true)
            {
                int read = await stream.ReadAsync(chunk, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                buffer.Write(chunk, 0, read);
                if (buffer.Length > byteLimit)
                {
                    truncated = true;
                    break;
                }
            }
            return new FetchResult(buffer.ToArray(), truncated, name,
                declaredLength > 0 ? declaredLength : buffer.Length);
        }
    }

    static string GetFileNameFromUrl(string url)
    {
        string path = url.Split('?', '#')[0];
        int slash = path.LastIndexOf('/');
        string tail = slash < 0 ? path : path[(slash + 1)..];
        try
        {
            return Uri.UnescapeDataString(tail);
        }
        catch (UriFormatException)
        {
            return tail;
        }
    }

    static string GetSuffix(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1 ? "" : fileName[dot..].ToLowerInvariant();
    }

    static bool IsBinary(string suffix, byte[] body)
    {
        if (suffix.Length > 0 && Array.IndexOf(BinarySuffixes, suffix) >= 0)
        {
            return true;
        }
        return LooksLikeImage(body) || body.Length > 0 && body[0] == 0;
    }

    static bool LooksLikeImage(byte[] body)
    {
        if (body.Length < 12)
        {
            return false;
        }
        return body[0] == 0x89 && body[1] == (byte)'P' && body[2] == (byte)'N' && body[3] == (byte)'G'
            || body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF
            || body[0] == (byte)'G' && body[1] == (byte)'I' && body[2] == (byte)'F' && body[3] == (byte)'8'
            || body[0] == (byte)'B' && body[1] == (byte)'M'
            || body[0] == (byte)'R' && body[1] == (byte)'I' && body[2] == (byte)'F' && body[3] == (byte)'F';
    }

    static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return unitIndex == 0
            ? ((long)size).ToString(CultureInfo.InvariantCulture) + " B"
            : size.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unitIndex];
    }

    static DecodedText Decode(byte[] body)
    {
        byte[] payload = StripBom(body);
        UTF8Encoding strictUtf8 = new(false, true);
        try
        {
            string text = strictUtf8.GetString(payload);
            return new DecodedText(LooksReadable(text), text);
        }
        catch (DecoderFallbackException)
        {
        }

        Encoding? gbk = TryGetEncoding(936);
        if (gbk != null)
        {
            // GBK 几乎什么字节都能凑出字来，所以解完还要看乱码比例，不能一解出东西就当成文本
            string text = gbk.GetString(payload);
            return new DecodedText(LooksReadable(text), text);
        }
        return new DecodedText(false, "");
    }

    static bool LooksReadable(string text)
    {
        int sample = Math.Min(text.Length, 4096);
        if (sample == 0)
        {
            return false;
        }
        int bad = 0;
        for (int index = 0; index < sample; index++)
        {
            char character = text[index];
            if (character == '\uFFFD' || character == '\u0000'
                || (char.IsControl(character) && character != '\t' && character != '\n' && character != '\r'))
            {
                bad++;
            }
        }
        return bad * 100 < sample * 5;
    }

    static Encoding? TryGetEncoding(int codePage)
    {
        // 本机 .NET 10 实测：GetEncoding(936) 抛的是 NotSupportedException（平台源码里没注册 CodePagesEncodingProvider，
        // 热编译插件也引不了那个包），所以 GBK 文本附件在这台机器上解不出来。缺了就返回 null 走「不是文本」分支，
        // 宁可如实说读不了，也不能抛出去把整个读文件流程炸掉，更不能把乱码当正文给模型
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    static byte[] StripBom(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            return body[3..];
        }
        return body;
    }

    sealed record Anchor(string MessageId, DateTime ReceivedAt);

    sealed record FetchResult(byte[] Body, bool Truncated, string FileName, long Length);

    sealed record DecodedText(bool IsText, string Content);

    sealed class FetchRejectedException(int statusCode) : Exception("服务器回 " + statusCode)
    {
        public int StatusCode { get; } = statusCode;
    }
}
