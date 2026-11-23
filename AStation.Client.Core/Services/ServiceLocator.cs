namespace AStation_Client.Services;

/// <summary>
/// 服务定位器，用于在 ViewModels/Pages 中获取单例服务实例。
/// 避免重复创建 HttpClient，统一管理 API 请求入口。
/// </summary>
public static class ServiceLocator
{
    // 单例初始化锁：??= 并非原子，并发首访可能各建一个 ApiService
    //（→ 两个 CookieContainer，登录态分裂）。Monitor 可重入，属性间嵌套调用安全。
    private static readonly object SyncRoot = new();

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
    private static SearchService? _search;
    private static HistoryService? _history;
    private static LiveDanmakuService? _liveDanmaku;
    private static FavoriteService? _favorite;
    private static UserProfileService? _userProfile;

    public static ApiService Api
    {
        get
        {
            lock (SyncRoot)
            {
                return _api ??= new ApiService();
            }
        }
    }

    public static PlayService Play
    {
        get
        {
            lock (SyncRoot)
            {
                return _play ??= new PlayService(Api);
            }
        }
    }

    public static DanmakuService Danmaku
    {
        get
        {
            lock (SyncRoot)
            {
                return _danmaku ??= new DanmakuService(Api);
            }
        }
    }

    public static CommentService Comment
    {
        get
        {
            lock (SyncRoot)
            {
                return _comment ??= new CommentService(Api);
            }
        }
    }

    public static LoginService Login
    {
        get
        {
            lock (SyncRoot)
            {
                return _login ??= new LoginService(Api);
            }
        }
    }

    public static HtmlParserService HtmlParser
    {
        get
        {
            lock (SyncRoot)
            {
                return _htmlParser ??= new HtmlParserService();
            }
        }
    }

    public static LiveService Live
    {
        get
        {
            lock (SyncRoot)
            {
                return _live ??= new LiveService(Api);
            }
        }
    }

    public static BangumiService Bangumi
    {
        get
        {
            lock (SyncRoot)
            {
                return _bangumi ??= new BangumiService(Api);
            }
        }
    }

    public static RankService Rank
    {
        get
        {
            lock (SyncRoot)
            {
                return _rank ??= new RankService(Api);
            }
        }
    }

    /// <summary>视频详情（标题/UP主/分P/播放数据）</summary>
    public static VideoDetailService VideoDetail
    {
        get
        {
            lock (SyncRoot)
            {
                return _videoDetail ??= new VideoDetailService(Api);
            }
        }
    }

    /// <summary>直播拉流（访客令牌 + FLV 档位）</summary>
    public static LivePlayService LivePlay
    {
        get
        {
            lock (SyncRoot)
            {
                return _livePlay ??= new LivePlayService(Api);
            }
        }
    }

    /// <summary>相关视频（同频道内容流）</summary>
    public static RelatedService Related
    {
        get
        {
            lock (SyncRoot)
            {
                return _related ??= new RelatedService(Api);
            }
        }
    }

    /// <summary>视频搜索</summary>
    public static SearchService Search
    {
        get
        {
            lock (SyncRoot)
            {
                return _search ??= new SearchService(Api);
            }
        }
    }

    /// <summary>播放历史（需登录）</summary>
    public static HistoryService History
    {
        get
        {
            lock (SyncRoot)
            {
                return _history ??= new HistoryService(Api);
            }
        }
    }

    /// <summary>直播间弹幕通道（长连接）</summary>
    public static LiveDanmakuService LiveDanmaku
    {
        get
        {
            lock (SyncRoot)
            {
                return _liveDanmaku ??= new LiveDanmakuService(Api);
            }
        }
    }

    public static FavoriteService Favorite
    {
        get
        {
            lock (SyncRoot)
            {
                return _favorite ??= new FavoriteService(Api);
            }
        }
    }

    public static UserProfileService UserProfile
    {
        get
        {
            lock (SyncRoot)
            {
                return _userProfile ??= new UserProfileService(Api);
            }
        }
    }
}
