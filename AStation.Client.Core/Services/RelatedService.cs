using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 相关视频服务。
///
/// 文档里没有「某视频的相关推荐」独立接口（/rest/pc-direct/related/recVideoList 已确认返回 HTML 404），
/// 所以这里用「频道内容流」按同频道取内容，再排除当前视频 —— 语义上最接近"相关视频"。
///
/// 接口：POST /rest/pc-direct/channel/resource/query/match
///   form: sortField = rankScore（综合）/ createTime / viewCount，channelId，page
///   响应: { result, total, channelInfo, feed[] }
///   feed[] 关键字段: dougaId / title / coverUrl / durationMillis / viewCount / danmakuCount /
///                    createTime（相对时间）/ user.name
/// </summary>
public class RelatedService
{
    private readonly ApiService _api;

    public RelatedService(ApiService api)
    {
        _api = api;
    }

    internal ApiService GetApiForTest() => _api;

    /// <summary>
    /// 取同频道的相关内容。
    /// </summary>
    /// <param name="channelId">频道 id（视频页 SSR 的 channel.id，如 84 = 主机单机）</param>
    /// <param name="excludeDougaId">要排除的稿件 id（通常是当前正在播的视频）</param>
    /// <param name="max">最多返回多少条</param>
    /// <param name="sortField">rankScore / createTime / viewCount</param>
    public async Task<List<RelatedVideoItem>> GetRelatedAsync(
        string channelId,
        string? excludeDougaId = null,
        int max = 20,
        string sortField = "rankScore",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return new List<RelatedVideoItem>();

        var form = new Dictionary<string, string>
        {
            ["sortField"] = sortField,
            ["channelId"] = channelId,
            ["page"] = "1"
        };

        try
        {
            var response = await _api.PostFormAsync<JsonNode>(
                "/rest/pc-direct/channel/resource/query/match", form, ct);

            return ParseFeed(response, excludeDougaId, max);
        }
        catch
        {
            return new List<RelatedVideoItem>();
        }
    }

    internal static List<RelatedVideoItem> ParseFeed(JsonNode? response, string? excludeDougaId, int max)
    {
        var list = new List<RelatedVideoItem>();

        if (response is not JsonObject obj) return list;
        if (ReadInt(obj["result"]) != 0) return list;
        if (obj["feed"] is not JsonArray feed) return list;

        foreach (var node in feed)
        {
            if (node is not JsonObject item) continue;

            var dougaId = ReadString(item["dougaId"]) ?? ReadLong(item["dougaId"]).ToString();
            if (string.IsNullOrWhiteSpace(dougaId) || dougaId == "0") continue;
            if (!string.IsNullOrEmpty(excludeDougaId) && dougaId == excludeDougaId) continue;

            list.Add(new RelatedVideoItem
            {
                DougaId = dougaId,
                Title = ReadString(item["title"]) ?? "(无标题)",
                Cover = ReadString(item["coverUrl"]) ?? string.Empty,
                Author = ReadString(item["user"]?["name"]) ?? string.Empty,
                DurationMillis = ReadLong(item["durationMillis"]),
                ViewCount = ReadLong(item["viewCount"]),
                DanmakuCount = ReadLong(item["danmakuCount"]),
                CreateTime = ReadString(item["createTime"]) ?? string.Empty
            });

            if (list.Count >= max) break;
        }

        return list;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;

        var raw = node.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim('"');
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

    private static int ReadInt(JsonNode? node) => (int)ReadLong(node);
}
