using System.ComponentModel;

namespace Suran.QQBotEnhance;

public class QQBotEnhanceConfig
{
    [DisplayName("官方机器人·AppID")]
    [Description("QQ 开放平台（bot.qq.com）应用详情页里的 AppID。本插件自己就是传输层，不填就什么都连不上")]
    public string AppId { get; set; } = "";

    [DisplayName("官方机器人·AppSecret")]
    [Description("与 AppID 配对的密钥。只落在这份本地配置里，不会进日志、也不会出现在任何工具回执中；一旦泄露去后台重置")]
    public string AppSecret { get; set; } = "";

    [DisplayName("事件·订阅成员进出与入群申请")]
    [Description("多订阅 intent 1<<24：有人进群、退群、申请加群时推事件进来。这类位要求应用真有该权限，没权限时平台会把连接直接关掉（关闭码 4014）并停止重连，确认后台已开通再打开")]
    public bool SubscribeMemberEvents { get; set; }

    [DisplayName("事件·订阅按钮点击")]
    [Description("多订阅 intent 1<<26：收到 INTERACTION_CREATE 才能知道谁点了 create_menu 的按钮，并回 PUT /interactions 应答（不应答对方客户端会一直转圈）。同样是权限位，没开通会被断开")]
    public bool SubscribeInteractions { get; set; }

    [DisplayName("事件·订阅消息审核结果")]
    [Description("多订阅 intent 1<<27：发出去的消息被平台审核拦下时能收到 MESSAGE_AUDIT_PASS/_REJECT，而不是只看见发送没报错。同样是权限位，没开通会被断开")]
    public bool SubscribeMessageAudit { get; set; }

    [DisplayName("改写·启用消息改写")]
    [Description("总开关。关掉后插件只做收发和查询工具，不再改动进入对话的消息文本，也不做菜单绑定")]
    public bool EnableRewrite { get; set; } = true;

    [DisplayName("改写·@显示成昵称")]
    [Description("官方消息里的 @ 是 <@openid> 这种富文本标记，模型看不出是谁、也看不出是不是在叫自己。开着会换成 @昵称，叫到机器人本人时补一个『你』")]
    public bool ResolveAtName { get; set; } = true;

    [DisplayName("改写·标注自己发的消息")]
    [Description("官方传输正常情况下不会把自己发出去的消息推回来；万一推了，开着会在那条前面标注是你自己刚说的，免得模型以为有人又问了一遍")]
    public bool MarkSelfEcho { get; set; } = true;

    [DisplayName("改写·绑定菜单回复")]
    [Description("把用户回复的编号（或点的按钮）对回本插件发出的选项菜单，在消息里注明这是对哪个活动的第几项、台账判定是否有效")]
    public bool BindMenuReplies { get; set; } = true;

    [DisplayName("通讯录·最大记录数")]
    [Description("超过这个数量会丢掉最久没出现的一条，取值 256-200000")]
    public int DirectoryCapacity { get; set; } = 8192;

    [DisplayName("通讯录·落盘保存")]
    [Description("写到 Storage/QQBotEnhance/directory.json，重启后老熟人不陌生。关掉就只在内存里")]
    public bool PersistDirectory { get; set; } = true;

    [DisplayName("输出·结果最大字符数")]
    [Description("查询类工具输出超过这个字符数截断并标注，取值 500-20000")]
    public int MaxOutputChars { get; set; } = 6000;

    [DisplayName("输出·成员最多列出")]
    [Description("找人/禁言名单一次最多列几条")]
    public int MaxMemberRows { get; set; } = 40;

    [DisplayName("附件·最大读取字节")]
    [Description("读聊天附件时下载的上限，超了只取前面一部分")]
    public int MaxFileBytes { get; set; } = 2 * 1024 * 1024;

    [DisplayName("附件·返回文本字符数")]
    [Description("附件解码后交给模型的字符上限")]
    public int MaxFileChars { get; set; } = 8000;

    [DisplayName("菜单·活动最多保留")]
    [Description("菜单台账里同时保留多少个活动（含已关闭的，用于对账），取值 20-2000")]
    public int MenuCapacity { get; set; } = 200;

    [DisplayName("菜单·落盘保存")]
    [Description("写到 Storage/QQBotEnhance/menu.json，重载插件后已发出去的菜单还能继续对账")]
    public bool PersistMenu { get; set; } = true;
}
