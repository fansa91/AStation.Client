using System.Text.Json.Nodes;
using AStation_Client.Models;
using AStation_Client.ViewModels;

namespace AStation_Client.Services;

/// <summary>
/// 排行榜服务。
/// 接口：GET /rest/pc-direct/rank/channel
///   参数：channelId（0=全站）、rankLimit、rankPeriod（DAY / THREE_DAYS / WEEK）
///   可选：subChannelId（真实子频道 id，-1 会返回空榜）
/// 频道筛选数据来自：GET /rest/pc-direct/page/queryNavigators（一级导航的 cid 即 channelId）
/// </summary>
public class RankService
{
    private readonly ApiService _api;

    public RankService(ApiService api)
    {
        _api = api;
    }

    /// <summary>周期 → 接口参数值</summary>
    public static string ToPeriodParam(RankPeriod period) => period switch
    {
        RankPeriod.ThreeDays => "THREE_DAYS",
        RankPeriod.Week => "WEEK",
        _ => "DAY"
    };

    public static string ToPeriodLabel(RankPeriod period) => period switch
    {
        RankPeriod.ThreeDays => "三日榜",
        RankPeriod.Week => "周榜",
        _ => "日榜"
    };

    /// <summary>
    /// 拉取排行榜。
    /// </summary>
    /// <param name="channelId">0 = 全站；其他值为一级频道 id</param>
    /// <param name="period">榜单周期</param>
    /// <param name="limit">条数，接口默认 30</param>
    public async Task<List<RankEntry>> GetRankListAsync(
        int channelId = 0,
        RankPeriod period = RankPeriod.Today,
        int limit = 30,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["channelId"] = channelId.ToString(),
            ["rankLimit"] = limit.ToString(),
            ["rankPeriod"] = ToPeriodParam(period)
        };

        JsonNode? json;
        try
        {
            json = await _api.GetAsync<JsonNode>("/rest/pc-direct/rank/channel", query, ct);
        }
        catch
        {
            return new List<RankEntry>();
        }

        return json is null ? new List<RankEntry>() : ParseRankList(json);
    }

    /// <summary>
    /// 解析排行榜响应为可直接绑定的条目列表。rankList 为空时返回空列表（非错误）。
    /// </summary>
    internal static List<RankEntry> ParseRankList(JsonNode root)
    {
        var result = new List<RankEntry>();
        if (root["rankList"] is not JsonArray list) return result;

        int rank = 1;
        foreach (var node in list)
        {
            if (node is null) continue;

            var title = ReadString(node["contentTitle"]) ?? ReadString(node["title"]) ?? "未知标题";
            var author = ReadString(node["userName"]) ?? ReadString(node["userId"]) ?? "未知作者";
            var cover = ReadString(node["videoCover"]) ?? ReadString(node["coverUrl"]) ?? string.Empty;
            var channelName = ReadString(node["channelName"]) ?? string.Empty;
            var resourceId = ReadString(node["contentId"]) ?? ReadString(node["dougaId"]) ?? string.Empty;

            long? view = ReadLong(node["viewCount"]);
            long? danmaku = ReadLong(node["danmuCount"]) ?? ReadLong(node["danmakuCount"]);
            long? durationMs = ReadLong(node["duration"]) ?? ReadLong(node["durationMillis"]);

            result.Add(new RankEntry
            {
                Rank = rank++,
                Title = title,
                Author = author,
                Cover = cover,
                ChannelName = channelName,
                ViewText = HomeViewModel.FormatCount(view),
                DanmakuText = HomeViewModel.FormatCount(danmaku),
                DurationText = HomeViewModel.FormatDuration(durationMs),
                ResourceId = resourceId
            });
        }

        return result;
    }

    /// <summary>
    /// 拉取可用于排行榜筛选的频道列表。第一项固定为「全站」。
    /// </summary>
    public async Task<List<RankChannel>> GetRankChannelsAsync(CancellationToken ct = default)
    {
        var channels = new List<RankChannel> { new() { ChannelId = 0, Name = "全站" } };

        try
        {
            var json = await _api.GetAsync<JsonNode>("/rest/pc-direct/page/queryNavigators", null, ct);
            if (json?["data"] is JsonArray navs)
            {
                foreach (var nav in navs)
                {
                    if (nav is null) continue;

                    var name = ReadString(nav["navName"]);
                    int cid = (int)(ReadLong(nav["cid"]) ?? 0);
                    if (string.IsNullOrWhiteSpace(name) || cid <= 0) continue;

                    // 番剧（link=/bangumilist）不在综合排行榜口径内，跳过
                    if (string.Equals(ReadString(nav["link"]), "/bangumilist", StringComparison.OrdinalIgnoreCase)) continue;

                    channels.Add(new RankChannel { ChannelId = cid, Name = name });
                }
            }
        }
        catch
        {
            // 拿不到频道时仅保留「全站」
        }

        return channels;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        var value = node.ToString().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is null) return null;
        return long.TryParse(node.ToString(), out long value) ? value : null;
    }
}
