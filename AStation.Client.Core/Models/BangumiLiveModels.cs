using System.Text.Json.Serialization;

namespace AStation_Client.Models;

/// <summary>
/// 番剧条目模型
/// </summary>
public class BangumiItem
{
    public string Title { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public int Episode { get; set; }

    /// <summary>番剧 id（形如 aa6000617 中的 6000617）</summary>
    public string BangumiId { get; set; } = string.Empty;

    /// <summary>集数文本，如 "全12话" / "更新至第8话"</summary>
    public string EpisodeText { get; set; } = string.Empty;

    /// <summary>追番人数文本，如 "123.9万"</summary>
    public string StatText { get; set; } = string.Empty;

    public override string ToString() => Title;
}

/// <summary>
/// 番剧列表响应模型
/// </summary>
public class BangumiResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("data")]
    public List<BangumiItem> Data { get; set; } = new();
}

/// <summary>
/// 直播条目模型（来源：live.acfun.cn 首页 SSR 内嵌 liveList）
/// </summary>
public class LiveItem
{
    public string Title { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;
    public int OnlineCount { get; set; }

    /// <summary>服务端已格式化的在线人数文本，如 "1.2万"</summary>
    public string OnlineText { get; set; } = string.Empty;

    public string LiveId { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;

    /// <summary>主播 uid —— 拉流接口的 visitorId（取 user.href）</summary>
    public string AuthorId { get; set; } = string.Empty;

    /// <summary>直播分类名，如 "游戏" / "虚拟偶像"</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>用于 UIA 测试与列表可读性</summary>
    public override string ToString() => $"{Title} - {Author}";
}

/// <summary>
/// 直播大分类（来源：live.acfun.cn/api/channel/list 的 channelFilters）
/// filterType=1 大分类；filterId：0 全部 / 1 游戏 / 2 其他 / 3 娱乐 / 4 虚拟偶像
/// </summary>
public class LiveCategory
{
    public int FilterType { get; set; } = 1;
    public int FilterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// 直播分类页的一次查询结果：分类标签 + 该分类下的直播间
/// </summary>
public class LiveChannelResult
{
    public List<LiveItem> Items { get; } = new();
    public List<LiveCategory> Categories { get; } = new();

    /// <summary>服务端返回的在播总数（可能大于当前返回条数）</summary>
    public int TotalCount { get; set; }
}

/// <summary>
/// 直播列表响应模型
/// </summary>
public class LiveResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("data")]
    public List<LiveItem> Data { get; set; } = new();
}
