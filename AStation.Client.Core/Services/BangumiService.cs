using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AStation_Client.Models;

namespace AStation_Client.Services;

/// <summary>
/// 番剧服务。
///
/// 接口现状（实测 2026-10）：
///   · /rest/pc-direct/bangumi/recommend/list  → 404（公开推荐接口已下线）
///   · /rest/pc-direct/feed/favorite/bangumi   → 需登录，返回「我的追番」
///   · GET /bangumilist                        → 200，SSR 直出番剧库（29 部真实数据）
///
/// 因此「番剧库」走 /bangumilist 的 HTML 解析，「我的追番」走登录态 JSON 接口。
/// 取流另走 /rest/pc-direct/play/playInfo/ksPlayJson?resourceType=1（在 PlayService）。
/// </summary>
public class BangumiService
{
    private readonly ApiService _api;

    public BangumiService(ApiService api)
    {
        _api = api;
    }

    /// <summary>
    /// 获取番剧库列表（解析 /bangumilist 的 SSR HTML）。
    /// 该页面无需登录，条目按追番人数排序。
    /// </summary>
    public async Task<List<BangumiItem>> GetBangumiListAsync(CancellationToken ct = default)
    {
        string? html;
        try
        {
            html = await _api.GetStringAsync("/bangumilist", null, ct);
        }
        catch
        {
            return new List<BangumiItem>();
        }

        if (string.IsNullOrWhiteSpace(html)) return new List<BangumiItem>();

        return ParseBangumiList(html);
    }

    /// <summary>
    /// 解析 /bangumilist 页面里的番剧卡片。
    /// 卡片结构：&lt;li class="ac-mod-li" data-aid="6000617"&gt;…img src…ac-mod-cover-stats…ac-mod-title title="…"…&lt;em&gt;全12话&lt;/em&gt;
    /// </summary>
    internal static List<BangumiItem> ParseBangumiList(string html)
    {
        var result = new List<BangumiItem>();

        // 按卡片切分，避免跨条目的贪婪匹配
        var chunks = html.Split("class=\"ac-mod-li\"", StringSplitOptions.RemoveEmptyEntries);
        if (chunks.Length <= 1) return result;

        foreach (var chunk in chunks)
        {
            // 只处理带 data-aid 的卡片片段（首段是 li 之前的页面头部）
            var aidMatch = Regex.Match(chunk, "data-aid=\"(\\d+)\"");
            if (!aidMatch.Success) continue;

            var bangumiId = aidMatch.Groups[1].Value;

            var titleMatch = Regex.Match(chunk, "ac-mod-title\"\\s+title=\"([^\"]*)\"");
            var spanMatch = Regex.Match(chunk, "<div class=\"ac-mod-title\"[^>]*>\\s*<span>(.*?)</span>", RegexOptions.Singleline);
            var title = titleMatch.Success && titleMatch.Groups[1].Value.Length > 0
                ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value)
                : spanMatch.Success
                    ? WebUtility.HtmlDecode(StripTags(spanMatch.Groups[1].Value))
                    : $"番剧 {bangumiId}";

            var coverMatch = Regex.Match(chunk, "<img\\s+src=\"([^\"]+)\"");
            var statMatch = Regex.Match(chunk, "ac-mod-cover-stats[^\"]*\"[^>]*>([^<]*)<");
            var episodeMatch = Regex.Match(chunk, "<em>([^<]*)</em>");

            var episodeText = episodeMatch.Success ? WebUtility.HtmlDecode(episodeMatch.Groups[1].Value.Trim()) : string.Empty;

            result.Add(new BangumiItem
            {
                BangumiId = bangumiId,
                Title = title,
                Cover = coverMatch.Success ? coverMatch.Groups[1].Value : string.Empty,
                EpisodeText = episodeText,
                Episode = ParseEpisodeNumber(episodeText),
                StatText = statMatch.Success ? WebUtility.HtmlDecode(statMatch.Groups[1].Value.Trim()) : string.Empty,
                Author = string.Empty,
                Link = $"https://www.acfun.cn/bangumi/aa{bangumiId}"
            });
        }

        return result;
    }

    /// <summary>从「全12话」「更新至第8话」之类的文本里取集数</summary>
    internal static int ParseEpisodeNumber(string? episodeText)
    {
        if (string.IsNullOrWhiteSpace(episodeText)) return 0;
        var match = Regex.Match(episodeText, "(\\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : 0;
    }

    // ───────────────────────────── 番剧详情（播放页用） ─────────────────────────────

    /// <summary>
    /// 获取番剧详情：标题、简介、播放量、追番数、总集数、当前集、mkey，以及完整剧集清单。
    ///
    /// 数据来源 GET /bangumi/aa{bangumiId} 的 SSR：
    ///   · window.bangumiData            → 元数据 + mkey + 当前集 videoId
    ///   · "items":[{"bangumiId":...}]   → 剧集清单（itemId / videoId / episodeName）
    /// 该页面免登录。
    /// </summary>
    public async Task<BangumiDetail?> GetBangumiDetailAsync(string bangumiId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bangumiId)) return null;

        string? html;
        try
        {
            html = await _api.GetStringAsync($"/bangumi/aa{bangumiId}", null, ct);
        }
        catch
        {
            return null;
        }

        return ParseBangumiDetail(html, bangumiId);
    }

    /// <summary>解析番剧播放页 HTML（拆分便于单测）</summary>
    internal static BangumiDetail? ParseBangumiDetail(string? html, string bangumiId)
    {
        var json = JsonExtractor.Extract(html, "window.bangumiData");
        if (json is null) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }

        if (root is not JsonObject obj) return null;

        var detail = new BangumiDetail
        {
            BangumiId = ReadString(obj["bangumiId"]) ?? bangumiId,
            Title = ReadString(obj["bangumiTitle"]) ?? ReadString(obj["title"]) ?? $"番剧 {bangumiId}",
            Introduction = ReadString(obj["bangumiIntro"]) ?? ReadString(obj["introduction"]) ?? string.Empty,
            Cover = ReadString(obj["bangumiCoverImageV"]) ?? ReadString(obj["image"]) ?? string.Empty,
            EpisodeName = ReadString(obj["episodeName"]) ?? string.Empty,
            Mkey = ReadString(obj["mkey"]) ?? string.Empty,
            CurrentVideoId = ReadString(obj["videoId"]) ?? string.Empty,
            ItemCount = (int)(ReadLong(obj["itemCount"])),
            PlayCount = ReadLong(obj["playCount"]),
            StowCount = ReadLong(obj["stowCount"]),
            LikeCount = ReadLong(obj["bangumiLikeCount"]),
            NeedPay = ReadBool(obj["needPay"]),
            UpdateStatus = ReadString(obj["extendsStatus"]) ?? ReadString(obj["latestItem"]) ?? string.Empty
        };

        // 剧集清单：页面里 "items":[{"bangumiId":...}] 的数组
        var itemsJson = ExtractEpisodeArray(html);
        if (itemsJson is not null)
        {
            try
            {
                if (JsonNode.Parse(itemsJson) is JsonArray episodes)
                {
                    foreach (var node in episodes)
                    {
                        if (node is null) continue;
                        var videoId = ReadString(node["videoId"]);
                        if (string.IsNullOrWhiteSpace(videoId)) continue;

                        detail.Episodes.Add(new BangumiEpisode
                        {
                            ItemId = ReadString(node["itemId"]) ?? string.Empty,
                            VideoId = videoId!,
                            Name = ReadString(node["episodeName"]) ?? $"第{detail.Episodes.Count + 1}集",
                            NeedPay = ReadBool(node["needPay"])
                        });
                    }
                }
            }
            catch
            {
                // 集列表解析失败不影响单集播放
            }
        }

        return detail;
    }

    /// <summary>定位并取出剧集数组</summary>
    private static string? ExtractEpisodeArray(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        // 剧集数组的标志是首元素带 bangumiId + episodeName
        int index = html.IndexOf("\"items\":[{\"bangumiId\"", StringComparison.Ordinal);
        if (index < 0) return null;

        int start = html.IndexOf('[', index);
        return start < 0 ? null : JsonExtractor.ExtractFrom(html, start);
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is null) return false;
        if (node is JsonValue value && value.TryGetValue<bool>(out bool b)) return b;
        return bool.TryParse(node.ToString().Trim('"'), out bool parsed) && parsed;
    }


    private static string StripTags(string value) => Regex.Replace(value, "<[^>]+>", string.Empty).Trim();

    /// <summary>
    /// 获取当前登录用户的追番列表。需要登录态 Cookie。
    /// </summary>
    public async Task<List<BangumiItem>> GetFavoriteBangumiAsync(int count = 20, CancellationToken ct = default)
    {
        var result = new List<BangumiItem>();

        try
        {
            var query = new Dictionary<string, string> { ["count"] = count.ToString() };
            var json = await _api.GetAsync<JsonNode>("/rest/pc-direct/feed/favorite/bangumi", query, ct);
            if (json is null) return result;

            // 兼容多种包裹形态：{ data: [...] } / { feedList: [...] } / 直接数组
            var array = json["data"] as JsonArray
                        ?? json["feedList"] as JsonArray
                        ?? json["list"] as JsonArray;
            if (array is null) return result;

            foreach (var node in array)
            {
                if (node is null) continue;

                var item = node["bangumi"] ?? node;
                var id = ReadString(item["bangumiId"]) ?? ReadString(item["id"]);
                if (string.IsNullOrEmpty(id)) continue;

                int episodeCount = ReadInt(item["itemCount"]) ?? ReadInt(item["latestItemCount"]) ?? 0;

                result.Add(new BangumiItem
                {
                    BangumiId = id,
                    Title = ReadString(item["bangumiTitle"]) ?? ReadString(item["title"]) ?? "未命名番剧",
                    Cover = ReadString(item["coverImageV2"])
                            ?? ReadString(item["coverImage"])
                            ?? ReadString(item["cover"])
                            ?? string.Empty,
                    Author = ReadString(item["updateInfo"]) ?? ReadString(item["author"]) ?? string.Empty,
                    Link = $"https://www.acfun.cn/bangumi/aa{id}",
                    Episode = episodeCount,
                    EpisodeText = episodeCount > 0 ? $"全{episodeCount}话" : string.Empty
                });
            }
        }
        catch
        {
            // 未登录 / 接口不可用：返回空，由页面提示
        }

        return result;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        var value = node.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim('"');
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is null) return null;
        return int.TryParse(node.ToString(), out int value) ? value : null;
    }

    private static long ReadLong(JsonNode? node)
    {
        if (node is null) return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out long l)) return l;
            if (value.TryGetValue<double>(out double d)) return (long)d;
        }
        return long.TryParse(node.ToString().Trim('"'), out long parsed) ? parsed : 0;
    }
}
