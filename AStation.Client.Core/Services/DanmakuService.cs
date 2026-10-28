using System.Text.Json.Serialization;

namespace AStation_Client.Services;

/// <summary>
/// 弹幕服务。
/// 对应接口：POST /rest/pc-direct/new-danmaku/list
/// 弹幕按分P挂载，resourceId 为分P的video id，resourceType=9。
/// </summary>
public class DanmakuService
{
    private readonly ApiService _api;

    public DanmakuService(ApiService api)
    {
        _api = api;
    }

    internal ApiService GetApiForTest() => _api;

    /// <summary>
    /// 获取弹幕列表。
    /// </summary>
    public async Task<DanmakuListResponse?> GetDanmakuAsync(
        string resourceId,
        int pcursor = 0,
        int count = 200,
        CancellationToken ct = default)
    {
        var formData = new Dictionary<string, string>
        {
            ["resourceId"] = resourceId,
            ["resourceType"] = "9",
            ["enableAdvanced"] = "true",
            ["pcursor"] = pcursor.ToString(),
            ["count"] = count.ToString(),
            ["sortType"] = "",
            ["asc"] = ""
        };
        return await _api.PostFormAsync<DanmakuListResponse>("/rest/pc-direct/new-danmaku/list", formData, ct);
    }

    /// <summary>
    /// 获取可直接渲染的弹幕列表（按出现时间排序，颜色转成 #RRGGBB）。
    /// resourceId 用「分P 的 video id」，番剧用 bangumiData.videoId（集级 id），resourceType 固定 9。
    /// </summary>
    public async Task<List<ClientDanmaku>> GetClientDanmakusAsync(
        string resourceId, int count = 500, CancellationToken ct = default)
    {
        var list = new List<ClientDanmaku>();
        if (string.IsNullOrWhiteSpace(resourceId)) return list;

        DanmakuListResponse? response;
        try
        {
            response = await GetDanmakuAsync(resourceId, 0, count, ct);
        }
        catch
        {
            return list;
        }

        if (response?.Danmakus is null) return list;

        foreach (var item in response.Danmakus)
        {
            if (string.IsNullOrWhiteSpace(item.Body)) continue;

            list.Add(new ClientDanmaku
            {
                Id = item.DanmakuId.ToString(),
                Text = item.Body!,
                TimeSeconds = item.PositionMs / 1000.0,
                Color = ToHexColor(item.Color),
                Mode = item.Mode switch
                {
                    4 => DanmakuMode.Bottom,
                    5 => DanmakuMode.Top,
                    _ => DanmakuMode.Scroll
                },
                FontSize = item.Size > 0 ? Math.Clamp(item.Size, 14, 36) : 24,
                UserId = item.UserId
            });
        }

        list.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return list;
    }

    /// <summary>接口的 color 是十进制 RGB（如 16777215），转成 #RRGGBB</summary>
    internal static string ToHexColor(long decimalColor)
    {
        uint rgb = (uint)decimalColor;
        return $"#{(rgb >> 16) & 0xFF:X2}{(rgb >> 8) & 0xFF:X2}{rgb & 0xFF:X2}";
    }
}

/// <summary>
/// 弹幕列表响应模型（参考 ACFUN_API.md 2.2 节）
/// </summary>
public class DanmakuListResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("danmakus")]
    public List<DanmakuItem> Danmakus { get; set; } = new();
}

public class DanmakuItem
{
    /// <summary>接口返回的是数字，不是字符串</summary>
    [JsonPropertyName("danmakuId")]
    public long DanmakuId { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>弹幕出现时间（毫秒）</summary>
    [JsonPropertyName("position")]
    public long PositionMs { get; set; }

    /// <summary>十进制 RGB 数值，如 16777215（白色）</summary>
    [JsonPropertyName("color")]
    public long Color { get; set; }

    /// <summary>1=滚动，4=底部，5=顶部</summary>
    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    /// <summary>字号</summary>
    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("userId")]
    public long UserId { get; set; }

    /// <summary>发送时间（毫秒时间戳）</summary>
    [JsonPropertyName("createTime")]
    public long CreateTimeMs { get; set; }

    [JsonPropertyName("likeCount")]
    public int LikeCount { get; set; }
}

/// <summary>
/// 将 AcFun 弹幕转换为客户端内部弹幕模型
/// </summary>
public class ClientDanmaku
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double TimeSeconds { get; set; } // 播放时间点（秒）
    public string Color { get; set; } = "#FFFFFF";
    public DanmakuMode Mode { get; set; } = DanmakuMode.Scroll;
    public double FontSize { get; set; } = 24;
    public long UserId { get; set; }
}

public enum DanmakuMode
{
    Scroll = 1,   // 滚动弹幕
    Bottom = 4,   // 底部弹幕
    Top = 5       // 顶部弹幕
}
