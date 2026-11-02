namespace AStation_Client.Services;

/// <summary>
/// 一条播放历史。字段取自 POST /rest/pc-direct/browse/history/list 的 histories[]。
///
/// 接口给的东西和首页卡片不太一样：封面是 cover、时长/进度已经是「07:26」「已看完」这种成品文案，
/// 浏览时间是毫秒时间戳。这里统一在 Service 里翻好，界面只负责显示。
/// </summary>
public class HistoryItem
{
    /// <summary>稿件 id（ac 号），跳播放页用</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>分P videoId；番剧是当前集的 videoId</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>1 = 番剧，2 = 普通视频</summary>
    public int ResourceType { get; set; } = 2;

    public string Title { get; set; } = string.Empty;

    /// <summary>UP 主昵称</summary>
    public string Up { get; set; } = string.Empty;

    public string Cover { get; set; } = string.Empty;

    /// <summary>总时长文案，如 07:26</summary>
    public string DurationText { get; set; } = string.Empty;

    /// <summary>进度文案，如「已看完」「02:10」</summary>
    public string ProgressText { get; set; } = string.Empty;

    /// <summary>已看百分比（0~100），画封面下方那条细进度条用</summary>
    public double ProgressPercent { get; set; }

    /// <summary>看过的时间，如「今天 14:30」「10月28日」</summary>
    public string BrowseTimeText { get; set; } = string.Empty;

    /// <summary>番剧集名 / 分P 标题，没有就空</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>副标题行：UP 主 · 分P/集名（自动拼，空值不占位）</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Up)) parts.Add(Up);
            if (!string.IsNullOrWhiteSpace(Subtitle)) parts.Add(Subtitle);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>是否看过一部分（没看完才显示进度条）</summary>
    public bool HasProgress => ProgressPercent > 0.5 && ProgressPercent < 99.5;

    /// <summary>UI Automation 用标题当列表项名称</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? base.ToString()! : Title;
}

/// <summary>播放历史的一页</summary>
public class HistoryPage
{
    public List<HistoryItem> Items { get; set; } = new();

    /// <summary>服务端返回的总条数</summary>
    public int TotalCount { get; set; }

    /// <summary>接口结果码，0 成功，-401 未登录</summary>
    public int Result { get; set; }

    public string? ErrorMessage { get; set; }
}
