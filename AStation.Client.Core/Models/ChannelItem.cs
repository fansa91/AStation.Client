namespace AStation_Client.Models;

public class ChannelItem
{
    public string Name { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;

    /// <summary>导航树里的 cid，等于排行榜接口的 channelId —— 频道卡片据此跳到该分区排行榜</summary>
    public int Id { get; set; }
}
