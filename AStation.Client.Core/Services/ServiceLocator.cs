namespace AStation_Client.Services;

/// <summary>
/// 服务定位器，用于在 ViewModels/Pages 中获取单例服务实例。
/// 避免重复创建 HttpClient，统一管理 API 请求入口。
/// </summary>
public static class ServiceLocator
{
    private static ApiService? _api;
    private static PlayService? _play;
    private static DanmakuService? _danmaku;
    private static CommentService? _comment;
    private static LoginService? _login;
    private static HtmlParserService? _htmlParser;
    private static LiveService? _live;
    private static BangumiService? _bangumi;
    private static RankService? _rank;
    private static VideoDetailService? _videoDetail;
    private static LivePlayService? _livePlay;
    private static RelatedService? _related;

    public static ApiService Api =>
        _api ??= new ApiService();

    public static PlayService Play =>
        _play ??= new PlayService(Api);

    public static DanmakuService Danmaku =>
        _danmaku ??= new DanmakuService(Api);

    public static CommentService Comment =>
        _comment ??= new CommentService(Api);

    public static LoginService Login =>
        _login ??= new LoginService(Api);

    public static HtmlParserService HtmlParser =>
        _htmlParser ??= new HtmlParserService();

    public static LiveService Live =>
        _live ??= new LiveService(Api);

    public static BangumiService Bangumi =>
        _bangumi ??= new BangumiService(Api);

    public static RankService Rank =>
        _rank ??= new RankService(Api);

    /// <summary>视频详情（标题/UP主/分P/播放数据）</summary>
    public static VideoDetailService VideoDetail =>
        _videoDetail ??= new VideoDetailService(Api);

    /// <summary>直播拉流（访客令牌 + FLV 档位）</summary>
    public static LivePlayService LivePlay =>
        _livePlay ??= new LivePlayService(Api);

    /// <summary>相关视频（同频道内容流）</summary>
    public static RelatedService Related =>
        _related ??= new RelatedService(Api);
}
