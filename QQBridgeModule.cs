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
using Alife.Function.QChat;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.QQBridge;

[Module("QQ官方机器人聊天增强",
    "把机器人收发的 QQ 消息读清楚、发得体：进对话的消息补上昵称名片、压掉读不懂的原始消息段、标出机器人自己的回声，并记住见过的 QQ号/名片/群名；能读聊天附件正文、按名字找人、查群与禁言状态、发长内容自动分段排版，还能发编号菜单并把对方回的编号对回菜单项。传输走 NapCat / LLOneBot / SnowLuma 这类协议端的 OneBot v11 接口，不是 QQ 开放平台官方机器人（bot.qq.com）那套",
    defaultCategory: "苏染的工具",
    LaunchOrder = 9000)]
public class QQBridgeModule(
    XmlFunctionCaller functionCaller,
    ILogger<QQBridgeModule> logger,
    Interactor<QQBridgeModule> interactor,
    QChatService qChatService) : ChatBehaviour, IConfigurable<QQBridgeConfig>
{
    public QQBridgeConfig Configuration { get; set; } = null!;

    // 单条上限取 QQ 官方文档里的消息长度红线，超过就会被协议端截断或拒发
    const int SegmentCharLimit = 4000;
    const int SegmentGapMilliseconds = 600;
    const int DefaultFileFetchTimeoutSeconds = 20;

    static readonly string[] BinarySuffixes =
    [
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".exe", ".dll", ".so", ".apk", ".ipa",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".mp3", ".mp4", ".wav", ".silk", ".flac", ".m4a", ".aac", ".ogg",
    ];

    static readonly HttpClient downloadClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    static readonly Regex OutgoingAtPattern = new(@"@([\p{L}\p{N}_\-·]{1,20})", RegexOptions.Compiled);

    PersonDirectory directory = null!;
    MenuLedger ledger = null!;
    MessageRewriter rewriter = null!;
    OneBotClient? client;
    readonly SemaphoreSlim connectLock = new(1, 1);
    // 协议端事件跑在 WebSocket 接收线程上，改写层和工具跑在别处，通讯录/台账/接口名单都得过这把锁
    readonly object stateGate = new();
    readonly HashSet<string> unsupportedActions = [];
    readonly List<string> orderedActions = [];
    string directoryFilePath = "";
    string menuFilePath = "";
    long botId;
    bool rewriteFailureReported;

    protected override Task OnAwake()
    {
        int capacity = Math.Clamp(Configuration.DirectoryCapacity, 256, 200000);
        directory = new PersonDirectory(capacity);
        ledger = new MenuLedger(Math.Clamp(Configuration.MenuCapacity, 20, 2000));
        rewriter = new MessageRewriter(directory) { Ledger = ledger };

        directoryFilePath = Path.Combine(AlifePath.StorageFolderPath, "QQBridge", "directory.json");
        menuFilePath = Path.Combine(AlifePath.StorageFolderPath, "QQBridge", "menu.json");
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
            Description = "QQ 官方机器人聊天增强：读清楚收到的消息、按名字找人、查群与禁言状态、读聊天附件、发长内容自动分段、发编号菜单并对账回复。",
            Explanation = "先决条件是 QQ聊天（OneBot）已经连上，本插件复用它的地址与密钥，只需要额外开一条连接，不用另配。"
                + "改写层是自动生效的：收到的消息里 [QQ号(名字)] 的名字会被补全，@ 后面是名字而不是号码，"
                + "[语音]、[视频: 链接]、[卡片: 标题]、[音乐分享]、[位置分享] 是压缩过的原始消息段，"
                + "标了 ·你自己 的那条是你自己刚发出去的回声，结尾〔〕里是编号菜单的对账结果。"
                + "发东西用 send_rich（比 QQ聊天 的 QChat 多自动分段和按名字@），给选项用 create_menu，"
                + "对方回的编号会自动对上；菜单收满、过期、冷却都会在对账里写清楚。"
                + "按名字找号先查本地通讯录 find_member，没记录再 sync_group_members 拉一次群成员表。"
                + "禁言、踢人、公告、入群审批这些改动群状态的动作在「群管」插件里，本插件只负责查。",
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        functionCaller.AddPlainAreas("send_rich", "create_menu");

        if (Configuration.EnableRewrite)
        {
            ChatBot.ChatSend += OnMessageText;
            ChatBot.PokeSend += OnMessageText;
        }
        interactor.Prompt(RewriteGuide);

        MaintainConnection();
        logger.LogInformation("[QQ增强] 已就绪：通讯录 {People} 人、{Groups} 个群，台账 {Menus} 个菜单（9 个工具）",
            directory.PersonCount, directory.GroupCount, ledger.Count);
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        ChatBot.ChatSend -= OnMessageText;
        ChatBot.PokeSend -= OnMessageText;
        // 先落盘再断开：Dispose 抛异常的话不能把通讯录和台账一起带走
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
        if (client != null)
        {
            client.EventReceived -= OnOneBotEvent;
            await client.DisposeAsync();
            client = null;
        }
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot, "bridge_state")]
    [Description("看 QQ 增强这侧的运行情况：这条连接是否在线、机器人自己的QQ号、群消息监听开关、通讯录与菜单台账规模、哪些接口当前协议端不支持")]
    public async Task BridgeState()
    {
        List<string> lines = [];
        bool connected = client?.IsConnected == true;
        lines.Add("[连接] " + (connected ? "在线" : "还没连上") + "｜" + qChatService.Configuration.Url
            + "｜机器人QQ号 " + (botId != 0 ? botId.ToString(CultureInfo.InvariantCulture) : "未知"));
        lines.Add("[改写] " + DescribeRewriteFlags());
        lock (stateGate)
        {
            lines.Add("[通讯录] " + directory.PersonCount + " 个人、" + directory.GroupCount + " 个群，"
                + (directory.LastSyncTime == null ? "还没整体同步过群成员表" : "上次同步 " + directory.LastSyncTime!.Value.ToString("MM-dd HH:mm"))
                + (Configuration.PersistDirectory ? "，落盘 " + directoryFilePath : "，只在内存里"));
            lines.Add("[菜单台账] " + ledger.Count + " 个活动" + (Configuration.PersistMenu ? "，落盘 " + menuFilePath : ""));
        }

        List<string> monitoring = [];
        // 这两代 QChat 里这个状态的类型名不一样（GroupState / GroupMessageSource），交给推断
        foreach (var pair in qChatService.GroupStates)
        {
            if (pair.Value.IsEnabled)
            {
                string known;
                lock (stateGate)
                {
                    known = directory.GroupName(pair.Key);
                }
                monitoring.Add(pair.Key + (known.Length > 0 ? "（" + known + "）" : ""));
            }
        }
        lines.Add("[群监听] " + (monitoring.Count == 0 ? "当前没有开启中的群" : string.Join("、", monitoring)));

        List<string> tried;
        lock (stateGate)
        {
            tried = orderedActions.ConvertAll(action =>
                action + (unsupportedActions.Contains(action) ? " 当前协议端不支持，已自动改用其他方式" : " 可用"));
        }
        foreach (string line in tried)
        {
            lines.Add("[接口] " + line);
        }
        if (connected == false)
        {
            lines.Add("连不上的话先看 QQ聊天 插件那边能不能连（地址、密钥、NapCat/LLOneBot/SnowLuma 有没有开正向 WS）");
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "group_status")]
    [Description("看一个群现在的状态：群名、人数、群主、是不是全群禁言、机器人自己在群里的身份和还剩多久解禁、还能@全体几次。只读，不改任何东西")]
    public async Task GroupStatus(
        [Description("群号")] long groupId)
    {
        JsonElement info = await CallAsync("get_group_info", new { group_id = groupId }, "群资料");
        List<string> lines = new()
        {
            "[" + groupId + "] " + OrUnknown(FindString(info, "group_name")),
        };
        AppendIfPresent(lines, info, "member_count", "人数");
        AppendIfPresent(lines, info, "max_member_count", "人数上限");
        AppendIfPresent(lines, info, "owner_id", "群主");
        AppendIfPresent(lines, info, "remark_name", "我给这个群改的备注");
        AppendIfPresent(lines, info, "group_memo", "群公告摘要");

        long allShut = FindNumber(info, "group_all_shut", "all_shut");
        lines.Add("全群禁言：" + (allShut < 0 ? "这个协议端不返回该字段，查不出来" : allShut > 0 ? "开启中" : "没开"));

        long selfShutUntil = FindNumber(info, "shut_up_me_timestamp");
        if (selfShutUntil > 0)
        {
            lines.Add("机器人自己被禁言到 " + UnixToString(selfShutUntil) + "，还剩 " + Remaining(selfShutUntil));
        }
        else if (selfShutUntil == 0)
        {
            lines.Add("机器人自己没被禁言");
        }
        else
        {
            await AppendSelfShutFromList(lines, groupId);
        }
        string selfRole;
        lock (stateGate)
        {
            selfRole = directory.RoleOf(groupId, botId);
        }
        if (selfRole.Length > 0)
        {
            lines.Add("机器人在群里的身份：" + selfRole + "（来自上次同步的成员表，要确认最新状态就再 sync_group_members 一次）");
        }

        await AppendAtAllRemain(lines, groupId);
        lock (stateGate)
        {
            directory.LearnGroup(groupId, FindString(info, "group_name"));
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "muted_members")]
    [Description("列出正在被禁言的群成员和解禁时间。优先用协议端的禁言名单接口，没有就翻成员表里带禁言时间戳的那些，两条路都走不通会直接说明")]
    public async Task MutedMembers(
        [Description("群号")] long groupId)
    {
        List<JsonElement> rows;
        string source;
        CallOutcome shutList = await TryCallAsync("get_group_shut_list", new { group_id = groupId });
        if (shutList.Data != null)
        {
            rows = ReadArray(shutList.Data.Value);
            source = "禁言名单接口";
        }
        else
        {
            CallOutcome members = await TryCallAsync("get_group_member_list", new { group_id = groupId });
            if (members.Data == null)
            {
                throw new Exception("这个协议端既没有禁言名单接口，也不返回成员表，查不到谁被禁言（"
                    + shutList.Failure + "／" + members.Failure + "）。可以逐个用 find_member 查已知的人");
            }
            rows = [];
            foreach (JsonElement member in ReadArray(members.Data.Value))
            {
                if (FindNumber(member, "shut_up_timestamp") > NowUnixSeconds())
                {
                    rows.Add(member);
                }
            }
            source = "成员表的禁言时间戳";
        }

        if (rows.Count == 0)
        {
            interactor.Poke(groupId + " 现在没有人被禁言（来源：" + source + "）");
            return;
        }
        int limit = Math.Clamp(Configuration.MaxMemberRows, 1, 500);
        List<string> lines = new() { "[" + groupId + " 禁言中 " + rows.Count + " 人，来源：" + source + "，最多显示 " + limit + " 人]" };
        lock (stateGate)
        {
            for (int index = 0; index < Math.Min(limit, rows.Count); index++)
            {
                JsonElement row = rows[index];
                long userId = FindNumber(row, "user_id");
                string name = OrUnknown(directory.DisplayName(userId, groupId, Configuration.PreferGroupCard));
                long until = FindNumber(row, "shut_up_timestamp", "shut_timestamp");
                lines.Add(userId + "｜" + name + "｜" + (until > 0 ? UnixToString(until) + " 解禁（还剩 " + Remaining(until) + "）" : "解禁时间未知"));
                directory.Learn(userId, FindString(row, "nickname", "card_or_nickname"), groupId, FindString(row, "card"));
            }
            if (source == "成员表的禁言时间戳")
            {
                directory.MarkSynced();
            }
        }
        if (rows.Count > limit)
        {
            lines.Add("还有 " + (rows.Count - limit) + " 人没显示，提高「输出·成员最多列出」或点名查 find_member");
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "find_member")]
    [Description("按名字在本地通讯录里找 QQ号，不联网、秒回。名片优先于昵称，QQ号也能直接搜。通讯录里没记录时先 sync_group_members 拉一次")]
    public async Task FindMember(
        [Description("要查的名字或QQ号片段")] string keyword,
        [Description("群号，填了会优先按这个群的名片匹配；0 表示不限定")] long groupId = 0,
        [Description("最多列几条，默认按配置")] int limit = 0)
    {
        string text = keyword.Trim();
        if (text.Length == 0)
        {
            throw new Exception("find_member 需要给 keyword，名字或QQ号片段都行");
        }
        int shownLimit = limit > 0 ? Math.Clamp(limit, 1, 200) : Math.Clamp(Configuration.MaxMemberRows, 1, 500);
        List<PersonDirectory.MatchedPerson> matched;
        int people;
        lock (stateGate)
        {
            matched = directory.Search(text, groupId, shownLimit);
            people = directory.PersonCount;
        }
        if (matched.Count == 0)
        {
            interactor.Poke("通讯录里没有名字含「" + text + "」的人（现在记了 " + people + " 个）。"
                + "这个群还没同步过的话用 sync_group_members group_id=" + groupId + " 拉一次成员表");
            return;
        }

        List<string> lines = new() { "[" + text + " 命中 " + matched.Count + " 人]" };
        lock (stateGate)
        {
            foreach (PersonDirectory.MatchedPerson person in matched)
            {
                lines.Add(person.UserId + "｜" + person.Name + "｜" + person.Source + "｜" + DescribeIdentity(person.UserId, groupId));
            }
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "sync_group_members")]
    [Description("拉一次群成员表灌进本地通讯录（QQ号、昵称、群名片、群身份），之后 find_member 和消息里的昵称补全就都靠它。人多时耗时较长")]
    public async Task SyncGroupMembers(
        [Description("群号")] long groupId,
        [Description("顺便列出的成员行数，0 表示只回统计不列人")] int maxRows = 0)
    {
        JsonElement data = await CallAsync("get_group_member_list", new { group_id = groupId }, "群成员表");
        List<JsonElement> members = ReadArray(data);
        if (members.Count == 0)
        {
            throw new Exception("成员表是空的，可能是这个协议端要分页参数或者机器人不是群成员");
        }

        int cardCount = 0;
        List<string> rows = [];
        int rowLimit = Math.Clamp(maxRows, 0, Math.Clamp(Configuration.MaxMemberRows, 1, 500));
        int totalPeople;
        lock (stateGate)
        {
            foreach (JsonElement member in members)
            {
                long userId = FindNumber(member, "user_id");
                string nickname = FindString(member, "card_or_nickname", "nickname");
                string card = FindString(member, "card");
                string role = FindString(member, "role");
                directory.Learn(userId, nickname.Length > 0 ? nickname : card, groupId, card);
                directory.LearnRole(groupId, userId, RoleText(role));
                if (card.Length > 0)
                {
                    cardCount++;
                }
                if (rows.Count < rowLimit)
                {
                    long shutUntil = FindNumber(member, "shut_up_timestamp");
                    rows.Add(userId + "｜" + (card.Length > 0 ? card : nickname) + "｜" + RoleText(role)
                        + JoinTime(FindNumber(member, "join_time"))
                        + (shutUntil > NowUnixSeconds() ? "｜禁言到 " + UnixToString(shutUntil) : "")
                        + (FindNumber(member, "unfriendly") > 0 ? "｜曾被举报" : ""));
                }
            }
            directory.MarkSynced();
            totalPeople = directory.PersonCount;
            if (Configuration.PersistDirectory)
            {
                directory.Save(directoryFilePath);
            }
        }

        List<string> lines = new()
        {
            "已同步 " + groupId + " 的成员表：" + members.Count + " 人（其中 " + cardCount + " 人有群名片），"
            + "通讯录累计 " + totalPeople + " 人",
        };
        lines.AddRange(rows);
        if (members.Count > rowLimit)
        {
            lines.Add("只列了前 " + rowLimit + " 人，找具体的人用 find_member，别靠这里刷屏");
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "read_attached_file")]
    [Description("读聊天里收到的文件正文（txt/md/代码/配置等），图片视频这类二进制只回名字大小和类型。可以直接把消息里 [文件: xxx, 下载地址: url] 的链接贴进来，也可以给群文件ID让插件自己换链接")]
    public async Task ReadAttachedFile(
        [Description("下载地址，优先用消息里已经给出来的那个")] string url = "",
        [Description("群文件用：群号")] long groupId = 0,
        [Description("群文件用：文件ID（消息里的 file= 或群文件列表里的ID）")] string fileId = "",
        [Description("私聊文件用：对方QQ号，配合文件ID换下载地址")] long userId = 0,
        [Description("文件名，给了会用来判断是不是文本文件")] string fileName = "")
    {
        string link = url.Trim();
        string name = fileName.Trim();
        if (link.Length == 0)
        {
            string wantedId = fileId.Trim();
            if (wantedId.Length == 0)
            {
                throw new Exception("read_attached_file 要么给 url，要么给 fileId（再带上 groupId 或 userId）");
            }
            JsonElement file = groupId != 0
                ? await CallAsync("get_group_file_url", new { group_id = groupId, file_id = wantedId }, "群文件下载地址")
                : await CallAsync("get_private_file_url", new { file_id = wantedId }, "私聊文件下载地址");
            link = FindString(file, "url");
            name = name.Length > 0 ? name : FindString(file, "file_name");
            if (link.Length == 0)
            {
                throw new Exception("协议端没给出这个文件的下载地址，可能是文件已过期或者机器人没有权限");
            }
        }
        if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase) == false)
        {
            throw new Exception("这个地址不是 http 链接，没法下载：" + Truncate(link, 120));
        }

        int byteLimit = Math.Clamp(Configuration.MaxFileBytes, 1024, 20 * 1024 * 1024);
        FetchResult fetched = await FetchAsync(link, byteLimit);
        string suffix = GetSuffix(name.Length > 0 ? name : fetched.FileName);
        if (IsBinary(suffix, fetched.Body))
        {
            interactor.Poke("“" + OrUnknown(name) + "”不是文本文件（" + HumanSize(fetched.Length)
                + (suffix.Length > 0 ? "，类型 " + suffix : "")
                + "），只报基本信息，不读正文。要看图用 QQ聊天 的 QImage 相关流程，要看视频同理");
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
    [Description("发一条排版好的 QQ 消息：开闭标签之间放正文，空行分段，超过单条上限会自动拆成多条按顺序发。正文里写 @某人 会自动换成真正的@（按通讯录匹配，重名就不换），写 @全体 就是@全体。想发图片/文件仍然用 QQ聊天 的 QImage、QFile")]
    public async Task SendRich(
        XmlExecutorContext context,
        [Description("group 发到群 / private 发到私聊")] string type,
        [Description("群号或对方QQ号")] long targetId,
        [Description("把 **加粗**、# 标题、[文字](链接) 这类 Markdown 语法换成 QQ 能正常显示的样子，默认换")] bool stripMarkdown = true)
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        OneBotMessageType messageType = ParseTargetType(type, "send_rich");
        if (targetId == 0)
        {
            throw new Exception("send_rich 缺 targetId（群号或对方QQ号）");
        }
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

        long atGroupId = messageType == OneBotMessageType.Group ? targetId : 0;
        int sent = 0;
        foreach (string segment in segments)
        {
            string text;
            lock (stateGate)
            {
                text = ResolveAtNames(segment, atGroupId);
            }
            await SendAsync(messageType, targetId, text);
            sent++;
            if (sent < segments.Count)
            {
                await Task.Delay(SegmentGapMilliseconds, DestroyCancellationToken);
            }
        }
        interactor.Poke("已发出 " + sent + " 条" + (messageType == OneBotMessageType.Group ? "群消息 " : "私聊消息 ")
            + targetId + "（正文 " + body.Length + " 字符，单条上限 " + SegmentCharLimit + "）");
    }

    [XmlFunction(FunctionMode.Content, "create_menu")]
    [Description("发一个带编号的选项菜单，并把这期菜单登记进台账。开闭标签之间一行一个选项（可以自带 1. 前缀，插件会重排）。QQ 个人号侧没有按钮回调，对方回编号就是点按钮，回的消息里会自动带上「是第几项、还有效吗」的对账")]
    public async Task CreateMenu(
        XmlExecutorContext context,
        [Description("group 发到群 / private 发到私聊")] string type,
        [Description("群号或对方QQ号")] long targetId,
        [Description("菜单标题，40 字以内")] string title,
        [Description("从发出算，多少秒后截止；0 表示不设截止时间")] int ttlSeconds = 0,
        [Description("截止时间，写 21:30 这种当天时刻（过了就顺延到次日）、或 2026-10-20 18:00、或 Unix 秒；和 ttl 二选一")] string until = "",
        [Description("总共收多少个选择，0 表示不限")] int max = 0,
        [Description("每个人最多选几次，0 表示不限")] int per = 0,
        [Description("每人只让选一次")] bool once = false,
        [Description("同一个人选同一项之后多少秒内不再算，0 表示不限")] int cooldownSeconds = 0,
        [Description("超限或过期后是否在收到的消息里写明「这条不要执行」，关掉就只提示不拦")] bool hard = true,
        [Description("所有选项共用一个名额（默认每个选项各记各的）")] bool shared = false,
        [Description("名额用满时在对账里额外提醒一句")] bool notify = false)
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        OneBotMessageType messageType = ParseTargetType(type, "create_menu");
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
                messageType == OneBotMessageType.Group ? targetId : 0,
                targetId, cleanTitle, options,
                Math.Clamp(ttlSeconds, 0, 30 * 24 * 3600), until,
                Math.Max(max, 0), Math.Max(per, 0), once, Math.Max(cooldownSeconds, 0),
                hard, shared, notify);
            text = BuildMenuText(record);
        }
        record.MessageId = await SendAsync(messageType, targetId, text);

        string row;
        lock (stateGate)
        {
            if (Configuration.PersistMenu)
            {
                ledger.Save(menuFilePath);
            }
            row = MenuLedger.FormatRow(record);
        }
        interactor.Poke(row + "｜已发到 "
            + (messageType == OneBotMessageType.Group ? "群 " : "私聊 ") + targetId
            + "｜短码 " + record.Code + "，对方直接回 1、2、3 就会对上这条；要收摊用 menu_manage action=close selector=" + record.Seq);
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
    // 改写与学习
    // ============================================================

    string OnMessageText(string message)
    {
        if (Configuration.EnableRewrite == false)
        {
            return message;
        }
        rewriter.ResolveAtName = Configuration.ResolveAtName;
        rewriter.PreferGroupCard = Configuration.PreferGroupCard;
        rewriter.NormalizeSegments = Configuration.NormalizeSegments;
        rewriter.MarkSelfEcho = Configuration.MarkSelfEcho;
        rewriter.BindMenuReplies = Configuration.BindMenuReplies;
        try
        {
            // 框架对 ChatSend 有兜底，对 PokeSend 没有（ChatBot 里那条循环不包 try），
            // 这里抛出去会把整批群消息刷掉，所以宁可原样放行也只报一次错
            lock (stateGate)
            {
                return rewriter.Rewrite(message);
            }
        }
        catch (Exception rewriteError)
        {
            if (rewriteFailureReported == false)
            {
                rewriteFailureReported = true;
                logger.LogWarning(rewriteError, "[QQ增强] 消息改写失败，这条原文进对话（后续同类失败不再重复报）");
            }
            return message;
        }
    }

    void OnOneBotEvent(OneBotBaseEvent baseEvent)
    {
        if (baseEvent is not OneBotMessageEvent messageEvent)
        {
            return;
        }
        if (botId == 0)
        {
            botId = baseEvent.SelfId;
            rewriter.BotId = botId;
        }
        long userId = messageEvent.Sender?.UserId ?? messageEvent.UserId;
        lock (stateGate)
        {
            directory.Learn(userId, messageEvent.Sender?.Nickname, messageEvent.GroupId, messageEvent.Sender?.Card);
            directory.LearnGroup(messageEvent.GroupId, messageEvent.GroupName);
        }
    }

    const string RewriteGuide =
        "本插件（QQ官方机器人聊天增强）会改写你收到的 QQ 消息，形态是这样的："
        + "发言标签 [QQ号(名字)] 里的名字优先用这个群的群名片，没有名片才用昵称，两者都没记到就只剩QQ号；"
        + "@ 后面是名字而不是QQ号，写成 @你 表示这条就是在叫你；"
        + "[语音]、[视频: 链接]、[卡片: 标题]、[音乐分享]、[位置分享] 是压缩过的原始消息段，里面的链接可以直接丢给 read_attached_file；"
        + "标签带 ·你自己 的那条是你自己刚发出去又被协议端推回来的回声，不要当成别人在追问；"
        + "[私聊消息(QQ号,昵称)] 之后的正文行不带发言人标签，那整块都是头里那个人的私聊内容；"
        + "消息结尾的〔〕是编号菜单对账结果，写了「有效」才继续按那个选项办，写了「不要执行」或「别猜」就别办，直接问清楚。"
        + "发长内容用 send_rich（自动分段、能按名字@人），给别人挑选用 create_menu；"
        + "要找人的QQ号先 find_member（本地通讯录），没记录再 sync_group_members 拉成员表。"
        + "禁言、踢人、公告、入群审批属于「群管」插件，本插件只提供查询。";

    // ============================================================
    // 连接与动作
    // ============================================================

    void MaintainConnection()
    {
        Task.Run(async () =>
        {
            int retrySeconds = Math.Max(qChatService.Configuration.AutoReconnectSeconds, 10);
            while (DestroyCancellationToken.IsCancellationRequested == false)
            {
                try
                {
                    await EnsureClientAsync();
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), DestroyCancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // 插件销毁时的正常退出，不该留一条没人看的失败任务
                    return;
                }
                catch (Exception connectError)
                {
                    logger.LogWarning("[QQ增强] 连不上 {Url}：{Message}", qChatService.Configuration.Url, connectError.Message);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(retrySeconds), DestroyCancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, DestroyCancellationToken);
    }

    async Task EnsureClientAsync()
    {
        await connectLock.WaitAsync(DestroyCancellationToken);
        try
        {
            if (client?.IsConnected == true)
            {
                return;
            }
            if (client == null)
            {
                client = new OneBotClient(qChatService.Configuration.Url, qChatService.Configuration.Token);
                client.EventReceived += OnOneBotEvent;
            }
            await client.ConnectAsync();
            botId = client.BotId != 0 ? client.BotId : qChatService.Configuration.BotId;
            rewriter.BotId = botId;
            logger.LogInformation("[QQ增强] 已连上 {Url}，机器人QQ号 {BotId}（这条连接只用于查询与学习，收消息仍走 QQ聊天）",
                client.Url, botId);
        }
        finally
        {
            connectLock.Release();
        }
    }

    async Task<JsonElement> CallAsync(string action, object parameters, string operationName)
    {
        CallOutcome outcome = await TryCallAsync(action, parameters);
        if (outcome.Data == null)
        {
            throw new Exception(operationName + "没成功：" + outcome.Failure);
        }
        return outcome.Data.Value;
    }

    // 三端对同一个动作名的支持不一样：1404/不支持的Api 记下来别再试，调用方据此回退
    async Task<CallOutcome> TryCallAsync(string action, object parameters)
    {
        lock (stateGate)
        {
            if (unsupportedActions.Contains(action))
            {
                return new CallOutcome(null, "这个协议端不支持 " + action);
            }
        }

        try
        {
            await EnsureClientAsync();
            OneBotClient? connection = client;
            if (connection == null)
            {
                return new CallOutcome(null, "连接还没建立");
            }
            JsonElement? data = await connection.CallActionAsync<JsonElement>(action, parameters);
            if (data == null || data.Value.ValueKind == JsonValueKind.Undefined)
            {
                return new CallOutcome(null, action + " 返回了空数据");
            }
            lock (stateGate)
            {
                if (orderedActions.Contains(action) == false)
                {
                    orderedActions.Add(action);
                }
            }
            return new CallOutcome(data, "");
        }
        catch (Exception callError)
        {
            // 失败原因跟着返回值走，不存字段：两个工具同时调用会串台
            string failure = DescribeFailure(callError.Message);
            if (callError.Message.Contains("RetCode: 1404") || failure.Contains("不支持"))
            {
                bool firstTime;
                lock (stateGate)
                {
                    firstTime = unsupportedActions.Add(action);
                }
                if (firstTime)
                {
                    logger.LogInformation("[QQ增强] 协议端不支持 {Action}，后续自动改用其他方式", action);
                }
            }
            return new CallOutcome(null, failure);
        }
    }

    async Task<long?> SendAsync(OneBotMessageType type, long targetId, string text)
    {
        CallOutcome sent = type == OneBotMessageType.Group
            ? await TryCallAsync("send_group_msg", new { group_id = targetId, message = text })
            : await TryCallAsync("send_private_msg", new { user_id = targetId, message = text });
        if (sent.Data == null)
        {
            throw new Exception("消息没发出去：" + sent.Failure);
        }
        long messageId = FindNumber(sent.Data.Value, "message_id", "real_id", "id");
        return messageId > 0 ? messageId : null;
    }

    string DescribeFailure(string detail)
    {
        string text = detail.Trim();
        if (text.Length == 0)
        {
            return "协议端没说明原因（SnowLuma 的说明文字在 wording 字段，框架客户端读不到）";
        }
        if (text.Contains("not supported") || text.Contains("不支持"))
        {
            return text;
        }
        if (text.Contains("RetCode: 1404"))
        {
            return "当前协议端没实现这个接口，换 NapCat/SnowLuma 或在协议端设置里打开对应功能";
        }
        if (text.Contains("RetCode: 1400") || text.Contains("RetCode: 1401") || text.Contains("RetCode: 1403"))
        {
            return "参数被判非法：" + text;
        }
        if (text.Contains("The task has canceled") || text.Contains("canceled"))
        {
            return "协议端 10 秒内没回话，可能掉线了或者动作被限流";
        }
        return text;
    }

    // NapCat 与 SnowLuma 不在群资料里给"我被禁到什么时候"，只能从禁言名单里找机器人自己
    async Task AppendSelfShutFromList(List<string> lines, long groupId)
    {
        if (botId == 0)
        {
            lines.Add("机器人自己是否被禁言查不出来：还没拿到自己的QQ号");
            return;
        }
        CallOutcome shutList = await TryCallAsync("get_group_shut_list", new { group_id = groupId });
        if (shutList.Data == null)
        {
            lines.Add("机器人自己是否被禁言查不出来：这个协议端既不在群资料里给时间戳，也没有禁言名单接口");
            return;
        }
        foreach (JsonElement row in ReadArray(shutList.Data.Value))
        {
            if (FindNumber(row, "user_id") != botId)
            {
                continue;
            }
            long until = FindNumber(row, "shut_timestamp", "shut_up_timestamp");
            lines.Add(until > 0
                ? "机器人自己被禁言到 " + UnixToString(until) + "，还剩 " + Remaining(until)
                : "机器人自己在禁言名单里，但没给解禁时间");
            return;
        }
        lines.Add("机器人自己没被禁言（禁言名单里没有它）");
    }

    async Task AppendAtAllRemain(List<string> lines, long groupId)
    {
        CallOutcome remain = await TryCallAsync("get_group_at_all_remain", new { group_id = groupId });
        if (remain.Data == null)
        {
            return;
        }
        JsonElement data = remain.Data.Value;
        if (FindBool(data, "can_at_all") == false)
        {
            lines.Add("@全体：现在不能@全体（一般是群限制）");
            return;
        }
        lines.Add("@全体：还能用，本群剩 " + CountText(FindNumber(data, "remain_at_all_count_for_group"))
            + "，机器人自己剩 " + CountText(FindNumber(data, "remain_at_all_count_for_uin")));
    }

    static string CountText(long value) => value < 0 ? "未知" : value + " 次";

    static bool FindBool(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement found) == false)
        {
            return false;
        }
        return found.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => found.GetInt32() != 0,
            JsonValueKind.String => found.GetString() == "true",
            _ => false,
        };
    }

    // ============================================================
    // 输出与格式
    // ============================================================

    string DescribeIdentity(long userId, long groupId)
    {
        string described = directory.Describe(userId, groupId);
        return described.Length > 0 ? described : "只记到QQ号";
    }

    string DescribeRewriteFlags()
    {
        List<string> enabled = [];
        if (Configuration.EnableRewrite == false)
        {
            return "总开关是关的，收到的消息原样进对话";
        }
        if (Configuration.ResolveAtName)
        {
            enabled.Add("@名字");
        }
        if (Configuration.PreferGroupCard)
        {
            enabled.Add("名片优先");
        }
        if (Configuration.NormalizeSegments)
        {
            enabled.Add("压缩消息段");
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
            line = Regex.Replace(line, @"^\d+\s*[.、)）:：]\s*", "");
            line = Regex.Replace(line, @"^[-*·]\s*", "");
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

    // 通讯录里唯一命中才换成真@，重名宁可留着原文也不要@错人
    string ResolveAtNames(string text, long groupId)
    {
        string result = text.Replace("@全体", "[CQ:at,qq=all]");
        return OutgoingAtPattern.Replace(result, match =>
        {
            string name = match.Groups[1].Value;
            if (directory.TryResolveByName(groupId, name, out long userId))
            {
                return "[CQ:at,qq=" + userId + "]";
            }
            return match.Value;
        });
    }

    static string UnixToString(long unixSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    static long NowUnixSeconds() => DateTimeOffset.Now.ToUnixTimeSeconds();

    static string Remaining(long unixSeconds)
    {
        TimeSpan left = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime - DateTime.Now;
        if (left.TotalSeconds <= 0)
        {
            return "已到期";
        }
        return left.TotalHours >= 1
            ? ((int)left.TotalHours) + " 小时 " + left.Minutes + " 分"
            : left.Minutes + " 分 " + left.Seconds + " 秒";
    }

    static string JoinTime(long unixSeconds)
        => unixSeconds > 0 ? "｜入群 " + DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("yyyy-MM-dd") : "";

    static string RoleText(string role) => role switch
    {
        "owner" => "群主",
        "admin" => "管理员",
        "member" => "普通成员",
        _ => role,
    };

    static string OrUnknown(string value) => value.Length > 0 ? value : "（未知）";

    static string FindString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement found))
            {
                return found.ValueKind switch
                {
                    JsonValueKind.String => found.GetString() ?? "",
                    JsonValueKind.Number => found.GetRawText(),
                    _ => "",
                };
            }
        }
        return "";
    }

    static long FindNumber(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement found))
            {
                if (found.ValueKind == JsonValueKind.Number && found.TryGetInt64(out long value))
                {
                    return value;
                }
                if (found.ValueKind == JsonValueKind.String && long.TryParse(found.GetString(), out long parsed))
                {
                    return parsed;
                }
            }
        }
        return -1;
    }

    static List<JsonElement> ReadArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            List<JsonElement> rows = [];
            foreach (JsonElement item in element.EnumerateArray())
            {
                rows.Add(item.Clone());
            }
            return rows;
        }
        // NapCat 系喜欢把数组包一层同名对象，LLOneBot 直接给数组
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in new[] { "members", "data", "list", "messages" })
            {
                if (element.TryGetProperty(name, out JsonElement inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    return ReadArray(inner);
                }
            }
        }
        return [];
    }

    static void AppendIfPresent(List<string> lines, JsonElement element, string name, string label)
    {
        string value = FindString(element, name);
        if (value.Length > 0)
        {
            lines.Add(label + "：" + value);
        }
    }

    static OneBotMessageType ParseTargetType(string type, string toolName)
    {
        string resolved = RequireOneOf(type, toolName + " 的 type", "group", "private");
        return resolved == "group" ? OneBotMessageType.Group : OneBotMessageType.Private;
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
        return unitIndex == 0 ? ((long)size).ToString(CultureInfo.InvariantCulture) + " B" : size.ToString("0.0") + " " + units[unitIndex];
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
        // 本机 .NET 10 实测：GetEncoding(936) 抛的是 NotSupportedException（平台源码里没有注册 CodePagesEncodingProvider，
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

    async Task<FetchResult> FetchAsync(string url, int byteLimit)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(DefaultFileFetchTimeoutSeconds));
        using HttpResponseMessage response = await downloadClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.IsSuccessStatusCode == false)
        {
            throw new Exception("下载失败，服务器回 " + (int)response.StatusCode);
        }

        string name = GetFileNameFromUrl(url);
        long declaredLength = response.Content.Headers.ContentLength ?? -1;
        try
        {
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
        catch (OperationCanceledException) when (DestroyCancellationToken.IsCancellationRequested == false)
        {
            throw new Exception("下载超时（" + DefaultFileFetchTimeoutSeconds + " 秒），这个地址可能已经过期或连不上");
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

    sealed record CallOutcome(JsonElement? Data, string Failure);

    sealed record FetchResult(byte[] Body, bool Truncated, string FileName, long Length);

    sealed record DecodedText(bool IsText, string Content);
}
