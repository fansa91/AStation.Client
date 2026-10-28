using System.Text.Json.Nodes;
using AStation_Client.Models;

namespace AStation_Client.Services;

/// <summary>
/// 直播服务。
/// 数据源（已实测可用）：
///   1. GET live.acfun.cn/api/channel/list?filters=[{"filterType":1,"filterId":X}]
///      → channelListData.liveList（直播间列表）+ channelFilters（大分类标签）
///      filterId：0 全部 / 1 游戏 / 2 其他 / 3 娱乐 / 4 虚拟偶像
///   2. GET live.acfun.cn/ 首页 SSR 内嵌 window.__INITIAL_STATE__.liveList（无分类时的兜底）
/// 取流另需访客令牌（id.app.acfun.cn/rest/app/visitor/login）+ api.kuaishouzt.com/rest/zt/live/web/getPlayUrls。
/// </summary>
public class LiveService
{
    private readonly ApiService _api;

    public LiveService(ApiService api)
    {
        _api = api;
    }

    /// <summary>默认分类（接口拿不到 channelFilters 时使用，取值来自 ACFUN_API.md 实测）</summary>
    internal static readonly LiveCategory[] DefaultCategories =
    {
        new() { FilterType = 1, FilterId = 0, Name = "全部" },
        new() { FilterType = 1, FilterId = 1, Name = "游戏" },
        new() { FilterType = 1, FilterId = 2, Name = "其他" },
        new() { FilterType = 1, FilterId = 3, Name = "娱乐" },
        new() { FilterType = 1, FilterId = 4, Name = "虚拟偶像" }
    };

    /// <summary>
    /// 按大分类拉取直播列表。
    /// </summary>
    /// <param name="filterId">0 全部 / 1 游戏 / 2 其他 / 3 娱乐 / 4 虚拟偶像</param>
    public async Task<LiveChannelResult> GetChannelListAsync(int filterId = 0, CancellationToken ct = default)
    {
        // filters 是 JSON 数组字符串，必须原样作为 query 值传递（BuildUrl 会做 URL 编码）
        var filters = $"[{{\"filterType\":1,\"filterId\":{filterId}}}]";
        var query = new Dictionary<string, string> { ["filters"] = filters };

        string? json;
        try
        {
            json = await _api.GetStringFromHostAsync(ApiService.LiveSiteHost, "/api/channel/list", query, ct);
        }
        catch
        {
            return new LiveChannelResult();
        }

        if (string.IsNullOrWhiteSpace(json)) return new LiveChannelResult();

        return ParseChannelList(json);
    }

    /// <summary>
    /// 解析 /api/channel/list 响应。
    /// 注意直播站把列表包在 channelListData 里，而分类标签在顶层 channelFilters。
    /// </summary>
    internal static LiveChannelResult ParseChannelList(string json)
    {
        var result = new LiveChannelResult();

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return result; }

        if (root is null) return result;

        // 列表：优先 channelListData.liveList，兜底顶层 liveList
        var list = root["channelListData"]?["liveList"] as JsonArray
                   ?? root["liveList"] as JsonArray;

        var totalNode = root["channelListData"]?["totalCount"] ?? root["totalCount"];
        if (totalNode is not null && int.TryParse(totalNode.ToString(), out int total))
        {
            result.TotalCount = total;
        }

        if (list is not null)
        {
            foreach (var node in list)
            {
                if (node is null) continue;
                result.Items.Add(ParseLiveItem(node));
            }
        }

        // 分类：channelFilters.liveChannelDisplayFilters[0].displayFilters
        var displayFilters = root["channelFilters"]?["liveChannelDisplayFilters"]?[0]?["displayFilters"] as JsonArray;
        if (displayFilters is not null)
        {
            foreach (var node in displayFilters)
            {
                if (node is null) continue;

                result.Categories.Add(new LiveCategory
                {
                    FilterType = ReadInt(node["filterType"], 1),
                    FilterId = ReadInt(node["filterId"], 0),
                    Name = node["name"]?.GetValue<string>() ?? string.Empty,
                    Cover = node["cover"]?.GetValue<string>() ?? string.Empty
                });
            }
        }

        if (result.Categories.Count == 0)
        {
            result.Categories.AddRange(DefaultCategories);
        }

        return result;
    }

    private static LiveItem ParseLiveItem(JsonNode node)
    {
        var user = node["user"];

        return new LiveItem
        {
            Title = node["title"]?.GetValue<string>() ?? "未命名直播",
            Author = user?["name"]?.GetValue<string>() ?? "未知主播",
            AuthorId = ReadString(user?["href"]) ?? ReadString(user?["id"]) ?? string.Empty,
            Avatar = user?["headUrl"]?.GetValue<string>() ?? string.Empty,
            Cover = FirstCover(node["coverUrls"]),
            OnlineCount = ReadInt(node["onlineCount"]),
            OnlineText = node["formatOnlineCount"]?.GetValue<string>() ?? string.Empty,
            LiveId = node["liveId"]?.GetValue<string>() ?? string.Empty,
            Link = node["href"]?.GetValue<string>() ?? string.Empty,
            Tag = ReadTag(node["type"])
        };
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        var raw = node.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim('"');
    }

    /// <summary>
    /// 获取直播站首页的热门直播列表（解析 SSR 内嵌数据）。分类接口不可用时的兜底。
    /// </summary>
    public async Task<List<LiveItem>> GetHotLiveListAsync(CancellationToken ct = default)
    {
        var result = new List<LiveItem>();

        string? html;
        try
        {
            html = await _api.GetStringFromHostAsync(ApiService.LiveSiteHost, "/", ct: ct);
        }
        catch
        {
            return result;
        }

        if (string.IsNullOrEmpty(html)) return result;

        var json = ExtractBalancedJson(html, "__INITIAL_STATE__");
        if (json is null) return result;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return result; }

        if (root?["liveList"] is not JsonArray list) return result;

        foreach (var node in list)
        {
            if (node is null) continue;
            result.Add(ParseLiveItem(node));
        }

        return result;
    }

    /// <summary>
    /// 从 HTML 中提取 <c>marker = {...}</c> 的平衡 JSON 对象。
    /// 直播站页面里的 __INITIAL_STATE__ 是单行巨型 JSON，必须按括号配平而不是正则。
    /// </summary>
    internal static string? ExtractBalancedJson(string html, string marker)
    {
        int markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return null;

        int start = html.IndexOf('{', markerIndex);
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return html.Substring(start, i - start + 1);
                    break;
            }
        }

        return null;
    }

    private static string FirstCover(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var url = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        return string.Empty;
    }

    private static string ReadTag(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            return obj["name"]?.GetValue<string>() ?? string.Empty;
        }
        return string.Empty;
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is null) return 0;
        if (int.TryParse(node.ToString(), out int value)) return value;
        return 0;
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null) return fallback;
        return int.TryParse(node.ToString(), out int value) ? value : fallback;
    }
}
