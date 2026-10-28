namespace AStation_Client.Pages;

/// <summary>
/// 播放页导航参数。
/// 普通视频用 resourceType=2（resourceId = dougaId），番剧用 resourceType=1（resourceId = bangumiId）。
/// VideoId 用于切集/切分P（番剧切集必须带 mkey，从番剧详情里取）。
/// </summary>
public sealed class VideoNavArgs
{
    public string ResourceId { get; init; } = string.Empty;
    public int ResourceType { get; init; } = 2;
    public string Title { get; set; } = string.Empty;

    /// <summary>指定分P / 集的 videoId，空则用接口返回的当前集</summary>
    public string? VideoId { get; init; }

    /// <summary>番剧播放授权 key</summary>
    public string? Mkey { get; init; }
}

/// <summary>
/// 播放页右侧列表的一项（分P 或 番剧集），两种内容共用一套模板
/// </summary>
public sealed class PlaylistEntry
{
    /// <summary>左侧序号文本，如 "1" / "P2"</summary>
    public string IndexText { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>副标题（时长 / 弹幕数 / 付费标记），空则不显示</summary>
    public string SubText { get; set; } = string.Empty;

    /// <summary>该条目的 videoId</summary>
    public string Key { get; set; } = string.Empty;

    public override string ToString() => Title;
}
