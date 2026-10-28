namespace AStation_Client.Models;

public class VideoItem
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string ViewCount { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string Thumbnail { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>
    /// 让 UI Automation 用视频标题作为列表项名称，而不是类型名
    /// </summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? base.ToString()! : Title;
}
