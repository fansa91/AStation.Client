namespace AStation_Client.Services;

/// <summary>收藏的四种资源分类（对应四个不同的列表接口）。</summary>
public enum FavoriteKind
{
    /// <summary>视频（favorite/dougaList）</summary>
    Douga = 0,

    /// <summary>番剧（favorite/bangumiList）</summary>
    Bangumi = 1,

    /// <summary>专辑（favorite/albumList）</summary>
    Album = 2,

    /// <summary>文章（favorite/articleList）</summary>
    Article = 3
}

/// <summary>收藏列表里的一条。</summary>
public class FavoriteItem
{
    /// <summary>资源 id：视频是 ac 号，番剧是 bangumiId</summary>
    public string ContentId { get; set; } = string.Empty;

    public FavoriteKind Kind { get; set; } = FavoriteKind.Douga;

    public string Title { get; set; } = string.Empty;

    public string Cover { get; set; } = string.Empty;

    /// <summary>UP 主 / 作者昵称（专辑是创建者）</summary>
    public string Up { get; set; } = string.Empty;

    /// <summary>时长文本（"12:34"），没有就是空串</summary>
    public string DurationText { get; set; } = string.Empty;

    /// <summary>有才显示封面右下角的时长角标（专辑/文章没有时长）</summary>
    public bool HasDuration => !string.IsNullOrWhiteSpace(DurationText);

    public int ViewCount { get; set; }

    public int StowCount { get; set; }

    /// <summary>收藏时间（毫秒时间戳，服务端可能不给）</summary>
    public long UpdateTimeMs { get; set; }

    /// <summary>收藏时间文本（"3天前" / "10月28日"），空串表示服务端没给</summary>
    public string UpdateText { get; set; } = string.Empty;

    /// <summary>副标题行：UP · 12.3万播放</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Up)) parts.Add(Up);
            if (ViewCount > 0) parts.Add($"{MetricFormat.Count(ViewCount)}播放");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>点击后能打开的目标类型（1 番剧 / 2 视频），0 表示本客户端还没有对应页面</summary>
    public int NavigateResourceType => Kind switch
    {
        FavoriteKind.Douga => 2,
        FavoriteKind.Bangumi => 1,
        _ => 0
    };

    public bool CanOpen => NavigateResourceType > 0 && !string.IsNullOrWhiteSpace(ContentId);

    public override string ToString() => Title;
}

/// <summary>数字的「万 / 亿」缩写。</summary>
internal static class MetricFormat
{
    /// <summary>12345 → "1.2万"，1234 → "1234"</summary>
    public static string Count(int value)
    {
        if (value >= 100_000_000) return (value / 100_000_000.0).ToString("0.#") + "亿";
        if (value >= 10_000) return (value / 10_000.0).ToString("0.#") + "万";
        return value.ToString();
    }
}

/// <summary>收藏列表的一页（游标分页，不是页码）。</summary>
public class FavoritePage
{
    public List<FavoriteItem> Items { get; set; } = new();

    /// <summary>下一页游标；空串表示没有下一页</summary>
    public string Cursor { get; set; } = string.Empty;

    public int Result { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>下一页游标有效且不是 "0"（接口用 "0" 表示首屏，尾页会返回空串或原样回 "0"）</summary>
    public bool HasMore => !string.IsNullOrWhiteSpace(Cursor) && Cursor != "0" && Items.Count > 0;
}
