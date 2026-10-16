# Suran.QQBotEnhance

QQ 官方机器人聊天增强：插件自己就是 QQ 开放平台官方机器人（`bot.qq.com`，API v2）的收发通道，同时把收进来的消息**读清楚**、把要发的消息**发得体**。不依赖「QQ聊天」（OneBot）插件，也不需要 NapCat / LLOneBot / SnowLuma 这类协议端——填了 AppID / AppSecret 就有连接，没填就什么都连不上。

前身是工作区的 `KiraAI-qqbot-fullmsg-bridge-plugin-main`（KiraAI 的 QQ 官方机器人 fullmsg bridge，Python + botpy）。那个插件是 monkey-patch 在 botpy 客户端上的，Alife 里没有可挂的官方机器人宿主，所以 4.0.x 先按 OneBot 传输把能力逐条重做了一遍；**5.0.0 把传输换成官方机器人接口本身**，OneBot 那条路整条摘掉。

## 版本
5.0.0

## 依赖
- Alife.Function.FunctionCaller（只有这一个。`manifest.json` 里不再声明 `Alife.Function.QChat`，源码里也不再有 OneBot 类型；`.buildcheck/QQBotEnhanceRuntimeCheck.csproj` 跟着只引 FunctionCaller，多引一个都会在装载时暴露出来）

## 这条路怎么走的
1. `POST /app/getAppAccessToken`（`api.bot.qq.com` 不通就退到 `bots.qq.com`，文档里两个地址都出现过）拿 `access_token`，`expires_in` 数字和字符串都吃；默认 7200 秒，**提前 5 分钟换新**（官方只保证到期前 60 秒内取新 token 时旧的还有效），重复请求不会刷新
2. `GET /gateway/bot` 拿 wss 地址（拿不到就退 `GET /gateway`），连上等平台下发 `op=10 Hello` 里的 `heartbeat_interval`，**不写死节奏**
3. `op=2 IDENTIFY` 上线：`token` 是 `"QQBot {access_token}"`，`intents` 按配置拼位掩码，`properties` 用文档里的 `$os/$browser/$device`
4. 收 `op=0 DISPATCH`：消息事件读成两行文本进对话，其他事件压成一行报进来；心跳按 `op=1` 带回最新 `s`，上一拍没回就把这条哑连接掐掉重连
5. **重连一律重发 IDENTIFY，不走 RESUME**——RESUME 不带 intents，续上的会话看着在线却收不到新订阅的事件
6. 关闭码里 `4001/4002/4010/4011/4012/4013/4014` 判成致命：这是配置或权限问题，重连没有用，插件停下来并在 `bridge_state` 里说清是哪一位没权限（4014 = intent 无权限）
7. 出站全部走 `/v2` REST，失败**只认 `err_code`**（官方原话：不要依据 `message` 判定成败），旧的频道接口只有 `code` 时也读得到

## 为什么要有这个插件
官方接口把消息原样丢过来时，模型看到的是：

1. 发言人只有一个 `member_openid`／`user_openid`，32 位十六进制串，看不出是谁；官方**没有群名片**，也**没有按 openid 查用户资料的接口**（`/users/@me` 只回机器人自己），昵称只在 `author.username` 里给一次
2. `@` 是富文本标记 `<@openid>`（也见过发送侧规范形态 `<qqbot-at-user id="…" />`，实测在群里被当纯文本显示），模型既不知道那是谁、也看不出是不是在叫自己
3. 附件在 `attachments[]` 里，`content_type` 才是类型（语音的字面量是 `voice` 而不是 mime）；结构化卡片是 `message_type=3 + ark_data`，只读 `content` 的框架会把整条消息读成空
4. 群里 @ 了机器人，但 `mentions[]` 里**不含机器人自己**——平台把自己摘掉了，得从「@ 里有、名单里没有」反推
5. 同一条 `msg_id` 官方会重复推送（文档原话），不做去重就会答两遍
6. 回复有窗口：群 5 分钟/最多 5 次、单聊 60 分钟/最多 4 次，过窗直接 `40034128`；`msg_seq` 还要自己递增，同 `msg_id`+同 `msg_seq` 会被 `40054005` 去重掉

本插件把这几件事一次做完，另外补上通讯录、附件正文读取、长消息分段、编号菜单台账与官方按钮对账。

## AI 函数
| 函数 | 说明 |
|---|---|
| `<bridge_state/>` | token 能不能取到、网关在不在线、订阅了哪几位、机器人自己的 openid（是不是反推的）、通讯录/台账规模、收发计数与最后一次失败原因、本地记到过哪些群 |
| `<group_status groupid/>` | 群名、人数、群分类、群公告、标签；机器人自己在群里的身份、能不能主动发言、收消息范围（`recv_msg_setting`）、入群时间 |
| `<muted_members groupid/>` | `restrict_chat_setting` 的读数：全群禁言规则 + 单独被禁言的人和解禁时间（官方没有 OneBot 那种禁言名单接口） |
| `<find_member keyword limit?/>` | 本地通讯录按名字或 openid 片段找人，不联网、秒回 |
| `<sync_group_members groupid maxrows?/>` | 翻完成员表灌进通讯录（每页 30 人），机器人自己的 openid 也在这一步对上；群里有多个机器人就不认领 |
| `<read_attached_file url filename?/>` | 读聊天附件正文（txt/md/代码/配置），二进制只报名字、大小、类型 |
| `<send_rich type targetid stripmarkdown?>正文</send_rich>` | 发排版好的消息：空行分段、超 4000 字符自动拆多条、`@名字` 换成 `<@openid>`、Markdown 降级；在回复窗口内走被动回复，过窗自动改发主动消息 |
| `<send_media type targetid mediatype url filename?/>` | 图片/视频/语音/文件：下载 → 上传换 `file_info` → 发 `msg_type=7`；语音只收 silk/mp3/wav/ogg |
| `<create_menu type targetid title ttlseconds? until? max? per? once? cooldownseconds? hard? shared? notify? buttons?>选项</create_menu>` | 发编号菜单并登记台账；`buttons=true` 发官方 markdown+键盘（≤5 行×5 列），被拒或选项超 25 个自动退回纯文本 |
| `<menu_manage action selector? minutes? reason? limit?/>` | 看台账统计、提前关闭、清空重选、延长截止时间（只动本地台账，不重发消息） |

`send_rich` / `send_media` / `create_menu` 的正文注册成**纯文本区**，Markdown 里的 `<`、`&` 不用转义。

改写层不是工具，是自动生效的：事件从网关进来就先改写，再交进对话（`RewriteInbound` 在 `interactor.ChatAsync`/`Poke` 之前跑），所以进上下文的已经是补好名字、压过段的样子；`OnAwake` 里另注入一段说明（`interactor.Prompt`）告诉模型这套形态怎么读。这和 4.0.x 不同——那一版传输在别人手里，只能在 `ChatSend`/`PokeSend` 钩子上改文本；5.0.0 自己就是传输，改写点搬到了源头，钩子内不再需要。群里没 @ 机器人的全量消息只 `Poke` 进缓冲区，不点模型；@ 了机器人和单聊才走 `ChatAsync`。

改写效果（`scratch/qqbotenhance-probe/probe-out.txt` 的真实输出，openid 是探针造的）：

```
[群聊消息(7C62A8D33B29509965198C6A99D36E53,研发(1)群)]
[6C628BF14402212160BFCFEB09CC9101(阿强)]: 收到
[群聊消息(7C62A8D33B29509965198C6A99D36E53,研发(1)群)]
[7D1640ECF941DC526AC990BD9E769904(苏染)]: 辛苦 @你 和 @小明
[群聊消息(7C62A8D33B29509965198C6A99D36E53,研发(1)群)]
[6C628BF14402212160BFCFEB09CC9102(小明)]: 1〔是对菜单「今晚值班」第 1 项「我」的选择｜第 1 次，有效〕
[群聊消息(7C62A8D33B29509965198C6A99D36E53,研发(1)群)]
[01542765B948AAB67BC3910602240884(QQ增强)·你自己]: 1
```

附件压成一行的样子：`[语音: 明天九点开会] https://…/v.wav`（转写是平台自带的 `asr_refer_text`）、`[图片: 链接]`、`[文件: note.txt 512 B] 链接`、`〔引用 {openid}：原文〕`、`〔卡片标题 - 描述〕`。按钮点回来的是 `[按钮] 苏染 点了选项按钮｜<台账判定>`，和回编号走同一套规则。

## 官方的硬约束怎么落进代码
- **只有 markdown 消息能挂按钮**，且要「互动事件」权限 → `create_menu buttons=true` 发 `msg_type=2` + 键盘，被拒就当场退回纯文本编号菜单，并在回报里写清为什么退
- **键盘 ≤5 行×5 列、`action.data` ≤100 字、`style` 只认 0/1**（超了 `40034029`、样式错 `305007`）→ 拆行在插件里做，标签超 20 字截断，`data` 写成「编号 短码」（`1 ~3`）好让台账唯一对上；`unsupport_tips` 告诉不支持按钮的客户端直接回编号
- **`INTERACTION_CREATE` 只有 type 11/12 要应答**，`PUT /interactions/{id}`（注意这条没有 `/v2` 前缀），同一个 id 只能回一次，不回对方客户端会一直转圈 → 收到就 `code=0` 应答，应答失败只记日志不抛
- **上传的 `file_name` 规则不对称**：类型 4 必给、类型 3 绝不能给（给了语音就变成文件卡片）、类型 1 可给、类型 2 不给；群上传的只能发群、单聊上传的只能发单聊 → 载荷构造函数按 `file_type` 分叉，探针逐条核过
- **文档把 `file_data`（base64 直传）标成「暂未支持」、只写 `url` 路线**，本地代码一直用 base64 → 这个差异没解决，上传失败时原话把两个可能性都写进报错，别当成已验证
- **被动回复只带 `msg_id`**（官方还有 `event_id` 一条路，本插件没用，代码里也不会出现），`msg_seq` 按 `(目标, msg_id)` 各自从 1 往上加，同 `msg_id`+同 `msg_seq` 会被去重 → 锚点记的是每个目标最近一次进来的那条消息 id 与它的到达时间，窗口过了就不带 `msg_id` 直接发主动消息；被动失败码是 `40034005/40034128/40034024/40034026/40034027` 这几个时当场改发一次主动，回报里写明"回复窗口已过，自动改发"
- **@全体没有**、`is_wakeup` 互动召回没做（那是另一套配额） → 正文里的 `@全体` 就是普通文字，`ResolveAtNames` 不碰它
- **`group_status`/`muted_members`/`sync_group_members` 三条接口官方标内邀/白名单**，无权限报 `err_code=11253` → 工具把这条原话转告（"代码变不出权限"），不静默留空
- **官方没有「列出机器人所在群」的接口**，「非 @ 消息也进上下文」也不是 intent 位而是后台的「接收所有消息」开关（`recv_msg_setting`）→ `bridge_state` 只报本地通讯录记到过的群，并提醒全量收消息要去后台开
- **私聊 `author.username` 恒为空串** → 单聊第一次见面只有 openid，名字得等他自己说过话或同步过成员表

## 配置项
`D:\Alife\Storage\Character\苏染\Configuration\Suran.QQBotEnhance.QQBotEnhanceModule.json`

| 配置 | 默认 | 作用 |
|---|---|---|
| 官方机器人·AppID / AppSecret | 空 | 不填不启动网关。密钥只落这份本地配置，不进日志、不出现在任何工具回执里（`TokenState` 只说"有效，剩 N 秒"）；泄露了去后台重置 |
| 事件·订阅成员进出与入群申请 | false | 多订 `1<<24`。**权限位**，没开通会被平台直接断开（4014）并停止重连 |
| 事件·订阅按钮点击 | false | 多订 `1<<26`，不订就收不到 `INTERACTION_CREATE`，按钮点了没反应 |
| 事件·订阅消息审核结果 | false | 多订 `1<<27`，能收到 `MESSAGE_AUDIT_PASS/_REJECT` |
| 改写·启用消息改写 | true | 关掉就是原文进对话，只留工具 |
| 改写·@显示成昵称 | true | 关掉后 `@openid` 保持原样 |
| 改写·标注自己发的消息 | true | 官方正常不推回声回来；万一推了，标 `·你自己` 让模型别接茬 |
| 改写·绑定菜单回复 | true | 关掉后消息结尾不带〔〕，按钮也不再对账 |
| 通讯录·最大记录数 | 8192 | LRU 上限（256～200000） |
| 通讯录·落盘保存 | true | 存 `QQBotEnhance/directory.json` |
| 输出·结果最大字符数 | 6000 | 工具回报的截断线（500～20000） |
| 输出·成员最多列出 | 40 | `sync_group_members`/`muted_members`/`find_member` 的行数上限 |
| 附件·最大读取字节 | 2 MB | 超过只下载前面一段并说明 |
| 附件·返回文本字符数 | 8000 | 解码后的正文上限 |
| 菜单·活动最多保留 | 200 | 台账容量（20～2000） |
| 菜单·落盘保存 | true | 存 `QQBotEnhance/menu.json` |

`改写·` 那几项每次调用都重读，不重载插件也能即时开关；订阅位改完必须重载插件才会重发 IDENTIFY。

## 落盘文件
- `D:\Alife\Storage\QQBotEnhance\directory.json` —— 通讯录（openid、入群昵称、群内身份、群名、最近同步时间）
- `D:\Alife\Storage\QQBotEnhance\menu.json` —— 编号菜单台账（计数、截止时间、短码、已发出的 message_id）

都是原子写（先 `.tmp` 再 `Move`），**不含 AppID/AppSecret**；但 openid 是个人信息，别把 Storage 目录分享出去。4.0.x 那份 OneBot 台账没有做数据迁移：旧台账里绑的是 QQ 号/群号，和 openid 不是一个命名空间，留着也接不上。

## 验证状态（诚实版）
- **全部未实弹。** 这台机器上没有 QQ 开放平台的 AppID/AppSecret，`api.bot.qq.com` 一次都没真连过。凡是 token 续期、网关上线、事件形状、按钮渲染、频率限制、内邀接口返回，都只有文档和本地参考代码作为依据，**没有任何一条是实测过的**。装上去第一次能收发消息才算开始验证。
- **编译**：`.buildcheck/runtime-gate.ps1` 两个门都 0 警告 0 错误
  - `QQBotEnhanceRuntimeCheck.csproj` → 在役代际：宿主 `D:\Alife.Client\resources\bin` 的框架 DLL + `D:\Alife\Runtime\PluginContext\CompiledPlugins` 的插件 DLL + .NET 共享运行时；现在只引 `Alife.Function.FunctionCaller`，用来证明这棵树真的不再依赖 QQ聊天
  - `QQBotEnhanceCheck.csproj` → AlifeSrc 源码代际（`ProjectReference`）
- **离线探针**：`scratch/qqbotenhance-probe`（`dotnet run`，输出看 `probe-out.txt`）**120 条判定全绿**，覆盖十段：注册面文档行、三种消息事件读成对话文本（语音转写/图片/文件/引用/卡片/时间戳）、非消息事件不当成消息、从 @ 反推机器人自己并标未确认、改写层（群名回填、裸 openid 补昵称、`@openid`→`@名字`/`@你`、回声不记账、回编号对上台账）、台账的会话隔离/短码/超范围/once/per/cooldown/hard 与 soft/歧义不许猜、键盘载荷的行列上限与 `data` 能被台账认回、拆条与 4000 上限、上传载荷的 `file_name`/`openid` 字段规则、`err_code` 判定与 401/非 JSON/空体、`IDENTIFY`/心跳帧构造与致命关闭码、通讯录与台账落盘读回
- **探针这一轮抓出来修掉的**：
  - 改写层的发言人正则**漏了行尾的 `]`**（`[openid(名)]: 正文` 整条匹配不上）→ 补昵称、`@` 替换、回声标注、菜单对账在真实入站形状上会**整块静默失效**，而且探针里原来那几条断言写成了"输入里已经有名字"的空判，看着绿其实什么都没测
  - `IDENTIFY` 的 `properties` 写成了 `os/browser/device`，文档里的键名带 `$` → 改成 `$os/$browser/$device`，并把这两帧拆成纯函数好离线核
  - 失败话术里 5xx 不带 HTTP 码，500/502/503 分不清哪个
  - 若干探针自身的错：`Check` 参数类型、`Array.ConvertAll` 用在数组上、`IndexOf(char,…,StringComparison)` 不存在、`FirstText` 的 `params` 要用数组传、被动错误码表按代码实际内容重列
- **已知空档（不是 5.0.0 引入的）**：`Encoding.GetEncoding(936)` 在这台机器的 .NET 10 上抛 `NotSupportedException`，宿主没注册 `CodePagesEncodingProvider`、热编译插件也引不了那个包 → **GBK 编码的文本附件读不出正文**。代码接得住（走二进制分支，话术如实写"这台机器还不支持 GBK"），要补只能宿主侧注册 provider
- **上限（官方侧决定的，不是代码没写）**：群资料/成员表/禁言读数三条踩在内邀权限上；昵称只有见过才有，`username` 是入群时的、不会跟着改名走；@全体发不出去；markdown 与按钮要后台配模板和开权限；主动消息有企业/未企业两档配额，超了 `40034100`
- **没做的**：流式回复（`stream_messages`，C2C 专用）、`input_notify` 输入状态提示、消息撤回、COS 分片上传三步流程、URL 报备（文档里没找到代码路径）、群黑名单与入群审批的写侧动作（后者属于「群管」插件）

## 打包
`.buildcheck\pack.ps1` 的 `$plugins` 列表里有 `Suran.QQBotEnhance`，产出平铺 zip（`manifest.json` + 11 个 `.cs`，README 不进），解压进 `D:\Alife\Storage\Plugins\Suran.QQBotEnhance\`。`manifest.json` 只声明 `Alife.Function.FunctionCaller`，`Version` 5.0.0。

装好后先在设置里填 AppID / AppSecret，重载插件，然后 `bridge_state` 看一眼：`[鉴权]` 能不能取到 token、`[网关]` 在不在线、`[订阅]` 里哪几位开着。若被 4014 拒了，把对应的事件开关关掉再重载——那一位权限后台还没给，重连没有用。
