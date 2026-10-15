# Suran.QQBridge

QQ 消息增强：把 OneBot 侧收到的消息**读清楚**、把要发的消息**发得体**。移植自工作区的 `KiraAI-qqbot-fullmsg-bridge-plugin-main`（KiraAI 的 QQ 官方机器人 fullmsg bridge 插件，Python），按 Alife 的传输现实（只有 OneBot v11：NapCat / LLOneBot / SnowLuma）逐条重做，源插件的能力一条没落下，落不下的都写明了为什么。

## 版本
4.0.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- Alife.Function.QChat（复用它的 OneBot 地址与密钥，自己另开一条正向 WS 连接，不反射、不改它的代码）

## 为什么要有这个插件
框架的 QQ聊天 层已经把 CQ 码转成了人能看的文本，但模型看到的仍然是三份残缺信息：

1. `[12345()]` 后面是空的 —— 框架只取 `Sender.Nickname`，名片（群内显示名）根本没参与，昵称为空时直接留一个空括号
2. `@<12345>` 是一串号码 —— 模型不知道那是谁，也没法在回复里按名字@人
3. `[CQ:record,file=...]`、`[CQ:xml,data=...]`、`[CQ:music,...]`、`[CQ:video,...]`、`[CQ:pos,...]` 这些段**原样穿过**框架的转换层，几百字符的转义 JSON 挤满上下文
4. 机器人自己发出去的消息会被协议端推回来（`post_type=message_sent` 也走 `OneBotMessageEvent` 判定），模型看到的是"别人说了一句我刚才说的话"
5. QQ 个人号侧没有按钮回调，模型给出的"请回复 1/2/3"没有任何机制记住对方选的是第几项、还有没有效

本插件把这几件事一次做完，另外补上通讯录、附件正文读取、长消息分段与编号菜单台账。

## AI 函数
| 函数 | 说明 |
|---|---|
| `<bridge_state/>` | 这条连接的在线状态、机器人QQ号、各群监听开关、通讯录/台账规模、当前协议端不支持的接口清单 |
| `<group_status groupid/>` | 只读查一个群：群名、人数、群主、是否全群禁言、Bot 自己在群里的身份与解禁倒计时、还能@全体几次 |
| `<muted_members groupid/>` | 列出正在被禁言的人和解禁时间。优先用禁言名单接口，没有就翻成员表里的禁言时间戳 |
| `<find_member keyword groupid? limit?/>` | 本地通讯录按名字/名片/QQ号片段找人，不联网、秒回 |
| `<sync_group_members groupid maxrows?/>` | 拉群成员表灌进通讯录（QQ号、昵称、群名片、群身份） |
| `<read_attached_file url? groupid? fileid? userid? filename?/>` | 读聊天里的文件正文（txt/md/代码/配置），二进制只报名字、大小、类型 |
| `<send_rich type targetid stripmarkdown? atsender?>正文</send_rich>` | 发排版好的消息：Markdown 降级、空行分段、超上限自动拆多条、`@某人` 换成真@ |
| `<create_menu type targetid title ttlseconds? until? max? per? once? cooldownseconds? hard? shared? notify?>选项</create_menu>` | 发编号菜单并登记进台账，对方回编号就等于点按钮 |
| `<menu_manage action selector? minutes? reason? limit?/>` | 看台账统计、提前关闭、清空重选、延长截止时间（只动本地台账，不重发消息） |

`send_rich` 与 `create_menu` 的正文注册成了**纯文本区**，Markdown 里的 `<`、`&` 不用转义。

改写层不是工具，是自动生效的：`ChatBot.ChatSend` / `PokeSend` 上挂了改写钩子（`LaunchOrder = 9000`，排在 MessageFilterService 的 10000 之前，所以补昵称、压段都发生在截断加时间戳之前）。同时注入一段提示，告诉模型改写后的形态怎么读。

改写效果（离线探针的真实输出）：

```
> [Q群消息(12345,测试群)]
[10001(值日小明)]:[语音: https://gchat.qpic.cn/a.amr] 明天值班
[10002(阿强)]:@你 帮我看看 [卡片: 群聊的分享内容] [音乐分享: https://y.qq.com/n/1] [位置分享]
[10003(小红)]:[视频: https://gchat.qpic.cn/b.mp4]
[90000(苏染)·你自己]:刚才是我自己发的
[10001(值日小明)]:2〔是对菜单「今晚吃什么」第 2 项「日料」的选择｜名额 1/2，有效；这个人的可选次数已经用满〕
```

## 与源插件的能力对照
| 源插件（QQ 官方机器人） | 这里的去处 | 说明 |
|---|---|---|
| 全消息桥接（非@消息也进上下文） | 不需要 | OneBot 侧本来就把群消息全量喂给框架，这是 Alife 的优势 |
| 发言人名片/昵称补全 | 改写层 + `PersonDirectory` | 名片优先，且**跨会话记住**（源插件靠每次调 API 查群成员） |
| `@号码` → `@名字` | 改写层 | 认不出的人留原文；`@` 自己写成 `@你` |
| 原始段压缩（json/xml/music/pos/record/video） | 改写层 | `[卡片: 标题]`、`[语音]`、`[音乐分享: 链接]`、`[位置分享]`，链接保留可直接转交 `read_attached_file` |
| 自发回声去重 | 改写层 `·你自己` | 不删（删了会丢上下文），标注身份让模型别接茬 |
| `qq_button` 内联键盘 + `INTERACTION_CREATE` 回调对账 | `create_menu` + `MenuLedger` | OneBot 没有按钮回调，编号回复就是点击通道。`once/max/per/ttl/until/cooldown/hard/shared/notify` 全部保留，短码 `~<36进制序号>` 对应源插件的按钮 token，三级匹配（短码 → message_id → 会话+选项）、收满/过期/冷却/歧义四种判定都照抄；**对不上时宁可列候选让模型问清楚，绝不猜** |
| 按钮台账落盘 | `menu.json` | 原子写（`.tmp` + `Move`），重启不丢计数，序号不会撞号 |
| `file_intake`（读附件正文） | `read_attached_file` | 2MB / 8000 字符 / 20 秒上限、UTF-8→BOM→GBK(936) 编码链、二进制后缀黑名单 + 魔数嗅探、`1.5 KB` 这种人话大小，全部移植；额外支持群文件 `file_id` 换下载地址 |
| `IdentityStore`（LRU 身份缓存） | `PersonDirectory` | 容量 8192（可配）、LRU 淘汰、名片按群分桶、群名缓存、本地模糊搜索打分（QQ号 4 / 精确 3 / 前缀 2 / 包含 1） |
| `GroupInfoCache` | 并入 `PersonDirectory` 的群名表 | 不需要单独的失败集合：三端拿不到群名就是拿不到，直接留空 |
| 管理侧限流（撤回 10QPS、禁言 60QPM…） | 交给 **Suran.GroupManager** | 写侧动作全部在群管插件里：`BanMember`/`UnbanMember`/`SetMemberCard`/`RecallMessage`/`KickMember`/`WholeGroupBan`/`PublishGroupNotice`/`QueryGroupNotices`/`DeleteGroupNotice`/`SetEssenceMessage`/`ListEssenceMessages`/`SetSpecialTitle`/`CheckJoinRequests`/`HandleJoinRequest`。本插件只做查询，工具的 Explanation 里明确写了这条分工，避免两处都能改群状态 |
| 不支持接口的记忆（`_INVITE_BLOCKED`） | `unsupportedActions` + 1404 判定 | 试过一次的"未实现"接口不再重试，`bridge_state` 里能看到清单 |
| Markdown 发送 / `msg_type=2` | `send_rich` 的 **Plainize 降级** | 官方侧 Markdown 要模板权限（没有就 `40034008/40034009/40034011/40034124/40034127`），OneBot 群文本也不渲染 `**` `#` `[x](y)`，所以是把 Markdown 拆成 QQ 能显示的样子，而不是发出去让人看星号 |
| 流式输入状态（`input_notify`）+ 编辑已发消息 | **不做** | 三端都没有 `set_input_notify` / `update_message` 这类动作，做不了 |
| 被动回复窗口记账（单聊 60 分钟/4 次、群聊 5 分钟/5 次、`msg_seq` 去重、`40034005` msg_id 过期） | **不需要** | OneBot 侧回复不限窗口、不需要 `msg_id`/`msg_seq`，这条约束本身不存在。真要写记账反而是给模型添乱 |
| 主动消息频控（单聊/群聊每月 4 条、`is_wakeup` 互动召回、频道 5 条/秒） | **不适用** | 个人号协议端没有这套配额；`40034100/40034122/40034123` 也不会出现 |
| 群黑名单/屏蔽列表 | **不做** | 官方 API 有对应接口，NapCat/LLOneBot/SnowLuma 三家都没有这个 action，无从回退 |
| silk 语音编码（ffmpeg→PCM→pysilk）+ 语音转文字 | **不做** | Alife 已有 TTS 通道（`ISpeechModel`，`voice=true` 直接发语音）；三端都没有 ASR 动作，转写无处可接 |
| `<sticker>` 标签注册（补 `supported_elements`） | **不适用** | 那是 KiraAI 框架自己的门槛。Alife 侧表情有 QChatService 的表情库与 Suran.QQEmoji，不需要往协议声明里塞词 |
| 32 位 `group_openid`/`user_openid`、必须被@才收得到、`msg_type=2` 被动回复 | **不存在** | OneBot 用真实 QQ 号/群号，非@消息也照收 |

## 三端差异与回退链（本插件实际用到的）
| 需要的信息 | 首选 | 回退 | 备注 |
|---|---|---|---|
| 群基本信息 | `get_group_info` | — | 三家同名同参；返回里 `owner_id`、`is_freeze` 只有 LLOneBot 有，缺了就不写这一行 |
| 是否全群禁言 | `get_group_info` 的 `group_shut_up`/`all_matched` | `group_all_shut` 的 `-1`/`0` | 三家的 0/-1 语义一致 |
| 自己禁到几点 | `shut_up_me_timestamp`（**仅 LLOneBot**） | 从成员表里挑自己的 `shut_up_timestamp` | 后两条路径拿不到就只说"不知道" |
| 禁言名单 | `get_group_shut_list`（NapCat、SnowLuma） | 翻 `get_group_member_list` 里带 `shut_up_timestamp` 的 | LLOneBot 没实现禁言名单接口，`retcode 1404`/`不支持的Api` 会被记住 |
| @全体剩余 | `get_group_at_all_remain` | — | NapCat/SnowLuma 返回字段一致（`can_at_all`、`remain_at_all_count_for_group`、`remain_at_all_count_for_uin`）；LLOneBot 没有就不写 |
| 成员表 | `get_group_member_list` | — | 返回可能是裸数组（LLOneBot）或包在 `members`/`data`/`list` 里，三种都吃 |
| 群文件下载地址 | `get_group_file_url` | `get_group_file_json_info` | 参数名按 `group_id`/`file_id` 传 |
| 发消息 | `send_group_msg` / `send_private_msg` | — | 三家一致；`message` 支持字符串，`[CQ:at,qq=all]` 三家都吃 |

写侧的兼容与实弹验证在 Suran.GroupManager / Suran.QQMedia 那边已经做过；本插件这几个查询动作里，`get_group_shut_list`、`shut_up_me_timestamp`、`get_group_at_all_remain` **还没实弹**（见下）。

## 从 QQ 官方机器人文档抄下来的几条硬约束
源插件的复杂度几乎都来自这几条，看清它们才知道哪些移植有意义：

- **发送侧只有 4 种消息形态**：`msg_type=0` 文本、`2` Markdown、`7` 富媒体（还有 ark/embed），Markdown 与键盘都要后台权限，缺权限直接报 `40034008/40034009/40034011/40034124/40034127`，键盘行列超限 `40034029` → 这里的 `send_rich` 因此选择降级而不是发语法
- **富媒体要先上传换 `file_info`，且有时效**，单聊与群聊的上传接口**不互通** → OneBot 的 `[CQ:image,file=url]` 没有这些约束，所以不移植上传层
- **被动回复窗口：单聊 60 分钟/每条最多 4 次（2026-01-10 从 5 次改成 4 次），群聊 5 分钟/5 次**，超时报 `40034005` → OneBot 无窗口，这条直接不适用
- **主动消息：单聊与群聊各"每月 4 条"**，用户可在客户端一键关掉主动消息，频道另有 5 条/秒、20 条/天/子频道；群内主动推送要群主打开「机器人主动在群聊内发言」（2026-06-22 起） → 说明源插件为什么要做 `msg_seq` 与去重，也说明个人号协议端为什么不需要
- **消息里带 URL 必须先在后台"消息 URL 配置"报备** → 这是源插件把链接压成文本时最难受的一条；OneBot 侧不报备也能发，所以 `read_attached_file` 才敢直接把链接留给模型
- **群成员列表每次最多 30 条、60 QPM 且标注"内邀接入中"**，入群审批 60 QPM → 这里 `sync_group_members` 一次拿整表（三端支持），但把列出行数默认压在 40 行、`muted_members` 同理，避免刷屏
- 官方建议**按 `err_code` 判失败，别信 `message`** → 本插件的失败描述同样优先给 retcode，SnowLuma 那句 `wording` 不在框架的返回模型里，缺了会写明"协议端没给原因"

## 配置项
`D:\Alife\Storage\Character\苏染\Configuration\Suran.QQBridge.QQBridgeModule.json`

| 配置 | 默认 | 作用 |
|---|---|---|
| 改写·开关 | true | 关掉就是原文进对话，本插件只留工具 |
| 改写·@后面换成名字 | true | 关掉后 `@<12345>` 保持原样 |
| 改写·优先用群名片 | true | 关掉后只用昵称 |
| 改写·压缩原始消息段 | true | 关掉后 json/xml/music/record 原样进上下文 |
| 改写·标注自己的回声 | true | 关掉后 `·你自己` 不写 |
| 改写·把回复对回菜单 | true | 关掉后消息结尾不带〔〕 |
| 通讯录·容量 | 8192 | LRU 上限（256～200000） |
| 通讯录·跨重启保存 | true | 存 `QQBridge/directory.json` |
| 输出·结果最大字符数 | 6000 | 所有工具回报的截断线（500～20000） |
| 输出·成员最多列出 | 40 | `sync_group_members`/`muted_members` 的行数上限 |
| 附件·最多下载字节 | 2 MB | 超过只下载前面一段并说明 |
| 附件·正文最多显示字符 | 8000 | 超过截断并说明还剩多少 |
| 菜单·台账容量 | 200 | 收满/过期的先淘汰，活动菜单不会被挤掉 |
| 菜单·跨重启保存 | true | 存 `QQBridge/menu.json` |

配置只在模块构造时灌一次值；`改写·` 那六项例外，改写层每次调用都重读，可以在不重载插件的情况下即时开关。

## 落盘文件
- `D:\Alife\Storage\QQBridge\directory.json` —— 通讯录（人、群名、身份、最近同步时间）
- `D:\Alife\Storage\QQBridge\menu.json` —— 编号菜单台账（含计数、截止时间、短码与已发出的 message_id）

两个文件都是原子写（先 `.tmp` 再 `Move`）。里面只有 QQ 号、昵称、群名片、群名和菜单文本，**不含任何密钥**；但 QQ 号是个人信息，别把 Storage 目录分享出去。

## 验证状态（诚实版）
- **编译**：`dotnet build .buildcheck/QQBridgeCheck.csproj` → 0 警告 0 错误
- **离线探针**：`scratch/qbridge-probe`（`dotnet run --project QQBridgeProbe.csproj`，或看 `probe-out.txt`）跑过九段核对：注册面文档行、改写层真实入站形状（名片/空昵称/段压缩/回声/菜单对账/@摘除/群头限定）、台账的 once/per/max/cooldown/shared/短码/超范围/歧义/过期与跨零点 `until`、存盘读回不撞号且计数还在、通讯录 LRU 与打分与精确重名拒猜、Plainize/4000 字拆条/超长单行硬拆、编码链与二进制嗅探与人话大小、选项解析与截断
- **探针顺手抓出来并修掉的坑**：`Close(menu, reason)` 私有重载被写丢导致内部关单全绑到公开重载、`ConvertAll(FormatShort)` 方法组类型推不出来、`UTF8Encoding(throwOnInvalidBytes:)` 单参构造不存在、报了编号超出选项数与只报短码两种回复会被**静默丢弃**（模型看不到任何对账）、`![图片](链接)` 被链接规则吃掉留下 `!图片：链接`、代码页 936 在没有注册 CodePagesEncodingProvider 的运行时抛的是 `NotSupportedException`（原来只接 `ArgumentException`，会把读文件流程炸穿）
- **没做的**：没连协议端实弹。`group_status`/`muted_members`/`sync_group_members`/`read_attached_file`/`send_rich`/`create_menu` 六条都要在 NapCat 或 LLOneBot 上真跑一遍才算数，其中 `get_group_shut_list`、`shut_up_me_timestamp`、`get_group_at_all_remain` 三家的支持面差异最容易在这里出岔子

## 打包
`pack.ps1` 的 `$plugins` 列表里有 `Suran.QQBridge`，产出扁平 zip（`manifest.json` + 五个 `.cs`），解压进 `D:\Alife\Storage\Plugins\Suran.QQBridge\` 即可。`manifest.json` 依赖声明 `Alife.Function.FunctionCaller` 与 `Alife.Function.QChat`。

启用顺序上没有硬依赖：QQ聊天 没连上时本插件也起得来，只是工具会报"还没连上协议端"，改写层照常工作（改写不需要连接）。
