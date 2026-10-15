using System.ComponentModel;

namespace Suran.QQBridge;

public class QQBridgeConfig
{
    [DisplayName("改写·启用消息改写")]
    [Description("总开关。关掉后插件只做查询工具，不再改动进入对话的消息文本，也不做菜单绑定")]
    public bool EnableRewrite { get; set; } = true;

    [DisplayName("改写·@显示成昵称")]
    [Description("框架把 [CQ:at,qq=123] 渲染成 @<123> 这种裸QQ号，模型看不出是谁、也看不出是不是在叫自己。开着会换成 @昵称，并在叫到机器人本人时补一个『（你）』")]
    public bool ResolveAtName { get; set; } = true;

    [DisplayName("改写·优先用群名片")]
    [Description("同一个人在群里既有昵称又有群名片时优先显示名片（框架只取昵称，名片字段一直是被丢掉的）。查不到名片就退回昵称")]
    public bool PreferGroupCard { get; set; } = true;

    [DisplayName("改写·归一化原始消息段")]
    [Description("语音、视频、json/xml 卡片、音乐、位置这些框架不处理的段会以 [CQ:record,file=超长串…] 原样进上下文，既占字数又读不懂。开着会换成一行简短标记，其中携带的链接仍然保留可用")]
    public bool NormalizeSegments { get; set; } = true;

    [DisplayName("改写·标注自己发的消息")]
    [Description("协议端会把机器人自己发出的消息也推回来（message_sent），框架不区分，模型会以为有人又问了一遍。开着会在文本对得上的那条前面标注是你自己刚说的")]
    public bool MarkSelfEcho { get; set; } = true;

    [DisplayName("改写·绑定菜单回复")]
    [Description("把用户回复的编号对回本插件发出的选项菜单，在消息里注明这是对哪个活动的第几项、台账判定是否有效。QQ 没有按钮回调，这条就是点按钮的替代通道")]
    public bool BindMenuReplies { get; set; } = true;

    [DisplayName("通讯录·记住昵称")]
    [Description("把见过的 QQ号→昵称/群名片 记下来，改名自动跟随。@改写和按名字找人都靠这份表")]
    public bool RememberIdentities { get; set; } = true;

    [DisplayName("通讯录·最大记录数")]
    [Description("超过这个数量会丢掉最久没出现的一条，取值 256-200000")]
    public int DirectoryCapacity { get; set; } = 8192;

    [DisplayName("通讯录·落盘保存")]
    [Description("写到 Storage/QQBridge/directory.json，重启后老熟人不陌生。关掉就只在内存里")]
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
    [Description("写到 Storage/QQBridge/menu.json，重载插件后已发出去的菜单还能继续对账")]
    public bool PersistMenu { get; set; } = true;
}
