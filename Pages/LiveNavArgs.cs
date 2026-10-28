namespace AStation_Client.Pages;

/// <summary>
/// 直播间导航参数（由直播列表页传入）
/// </summary>
public sealed class LiveNavArgs
{
    /// <summary>主播 uid（拉流接口的 visitorId）</summary>
    public string AuthorId { get; init; } = string.Empty;

    /// <summary>直播 id（拉流接口的 liveId）</summary>
    public string LiveId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string AuthorAvatar { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string OnlineText { get; init; } = string.Empty;
    public string Cover { get; init; } = string.Empty;
}
