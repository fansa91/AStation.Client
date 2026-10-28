using System.Text.Json.Serialization;

namespace AStation_Client.Services;

/// <summary>
/// 视频搜索：GET /rest/pc-direct/search/video?keyword=&amp;page=&amp;size=
/// 只覆盖视频（用户 / 番剧的搜索接口返回 HTML 页，暂未接入），结果映射成
/// 与首页一致的 <see cref="AStation_Client.Models.VideoItem"/>，卡片可以直接复用。
/// </summary>
public class SearchService
{
    private readonly ApiService _api;

    public SearchService(ApiService api)
    {
        _api = api;
    }

    public async Task<SearchResult> SearchVideosAsync(string keyword, int page = 1, int size = 20, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["keyword"] = keyword,
            ["page"] = page.ToString(),
            ["size"] = size.ToString()
        };

        var response = await _api.GetAsync<SearchVideoResponse>("/rest/pc-direct/search/video", query, ct);
        if (response?.Result != 0 || response.VideoList is null)
        {
            return new SearchResult();
        }

        var result = new SearchResult { Total = response.TotalNum };
        foreach (var item in response.VideoList)
        {
            if (string.IsNullOrWhiteSpace(item.Title)) continue;

            result.Items.Add(new Models.VideoItem
            {
                Title = item.Title,
                Author = item.UserName ?? string.Empty,
                ViewCount = item.ViewCountInfo ?? string.Empty,
                Duration = item.PlayDuration ?? string.Empty,
                Thumbnail = item.CoverUrl ?? string.Empty,
                ResourceId = ResolveResourceId(item)
            });
        }

        return result;
    }

    /// <summary>优先用 contentId（acId），缺失时退回 videoId</summary>
    private static string ResolveResourceId(SearchVideoItem item)
    {
        if (item.ContentId > 0) return item.ContentId.ToString();
        if (long.TryParse(item.VideoId, out long videoId) && videoId > 0) return videoId.ToString();
        return string.Empty;
    }
}

public class SearchResult
{
    public long Total { get; set; }
    public List<Models.VideoItem> Items { get; } = new();
}

public class SearchVideoResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    /// <summary>命中总数</summary>
    [JsonPropertyName("totalNum")]
    public long TotalNum { get; set; }

    [JsonPropertyName("videoList")]
    public List<SearchVideoItem>? VideoList { get; set; }
}

public class SearchVideoItem
{
    /// <summary>acId（数字），播放页 resourceType=2 要用它</summary>
    [JsonPropertyName("contentId")]
    public long ContentId { get; set; }

    /// <summary>注意：这个字段服务端以「字符串」下发（"videoId":"26280935"），不能声明成数字</summary>
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }

    /// <summary>时长文本，如 26:48</summary>
    [JsonPropertyName("playDuration")]
    public string? PlayDuration { get; set; }

    /// <summary>播放量文本，如「17.8万次播放」</summary>
    [JsonPropertyName("viewCountInfo")]
    public string? ViewCountInfo { get; set; }

    [JsonPropertyName("danmakuCountInfo")]
    public string? DanmakuCountInfo { get; set; }

    /// <summary>一行摘要，形如「17.8万次播放  2021-03-13」</summary>
    [JsonPropertyName("displayInfo")]
    public string? DisplayInfo { get; set; }
}
