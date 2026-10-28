namespace AStation_Client.Models;

/// <summary>
/// 排行榜周期。接口参数值：DAY / THREE_DAYS / WEEK（ACFUN_API.md 实测）
/// </summary>
public enum RankPeriod
{
    Today,
    ThreeDays,
    Week
}

/// <summary>
/// 排行榜条目（已格式化为可直接绑定的字符串）
/// </summary>
public class RankEntry
{
    public int Rank { get; set; }
    public string RankText => Rank.ToString();

    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;

    /// <summary>播放量，已格式化（如 1.7万）</summary>
    public string ViewText { get; set; } = string.Empty;

    /// <summary>弹幕数，已格式化</summary>
    public string DanmakuText { get; set; } = string.Empty;

    public string DurationText { get; set; } = string.Empty;

    /// <summary>视频所属二级频道名，如「生活日常」</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>视频 id，用于跳转播放页</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>元信息行：频道 · 播放 · 弹幕</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ChannelName)) parts.Add(ChannelName);
            if (!string.IsNullOrWhiteSpace(ViewText)) parts.Add($"{ViewText} 播放");
            if (!string.IsNullOrWhiteSpace(DanmakuText)) parts.Add($"{DanmakuText} 弹幕");
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => $"{RankText}. {Title}";
}

/// <summary>
/// 排行榜可选频道（来自 /rest/pc-direct/page/queryNavigators 的一级导航）
/// </summary>
public class RankChannel
{
    public int ChannelId { get; set; }
    public string Name { get; set; } = string.Empty;

    public override string ToString() => Name;
}
