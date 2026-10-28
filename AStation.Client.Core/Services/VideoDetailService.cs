using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 视频详情服务。
/// 对应页面：GET /v/ac{dougaId}
///
/// 页面 SSR 内嵌两块数据（实测）：
///   · window.videoInfo → 标题 / 简介 / 封面 / 播放·弹幕·评论·点赞·收藏数 / 频道 / videoList（分P）
///   · "user":{...}     → UP 主 name / href / headUrl / fanCountValue
/// 弹幕按分P挂载，videoList[].id 才是弹幕接口的 resourceId（不是 dougaId）。
/// </summary>
public class VideoDetailService
{
    private readonly ApiService _api;

    public VideoDetailService(ApiService api)
    {
        _api = api;
    }

    public async Task<VideoDetail?> GetVideoDetailAsync(string dougaId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dougaId)) return null;

        string? html;
        try
        {
            html = await _api.GetStringAsync($"/v/ac{dougaId}", null, ct);
        }
        catch
        {
            return null;
        }

        return Parse(html, dougaId);
    }

    /// <summary>
    /// 解析视频页 HTML（拆分出来便于单测）
    /// </summary>
    internal static VideoDetail? Parse(string? html, string dougaId)
    {
        var json = JsonExtractor.Extract(html, "window.videoInfo");
        if (json is null) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }

        if (root is not JsonObject obj) return null;

        var detail = new VideoDetail
        {
            DougaId = ReadString(obj["dougaId"]) ?? dougaId,
            Title = ReadString(obj["title"]) ?? $"ac{dougaId}",
            Description = ReadString(obj["description"]) ?? string.Empty,
            CoverUrl = ReadString(obj["coverUrl"]) ?? string.Empty,
            ViewCount = ReadLong(obj["viewCount"]),
            DanmakuCount = ReadLong(obj["danmakuCount"]),
            CommentCount = ReadLong(obj["commentCount"]),
            LikeCount = ReadLong(obj["likeCount"]),
            StowCount = ReadLong(obj["stowCount"]),
            BananaCount = ReadLong(obj["bananaCount"]),
            CreateTimeMillis = ReadLong(obj["createTimeMillis"]),
            CurrentVideoId = ReadString(obj["currentVideoId"]) ?? string.Empty,
            ChannelName = ReadString(obj["channel"]?["name"]) ?? ReadString(obj["channel"]?["parentName"]) ?? string.Empty,
            ChannelId = ReadString(obj["channel"]?["id"]) ?? string.Empty,
            ParentChannelId = ReadString(obj["channel"]?["parentId"]) ?? string.Empty
        };

        // UP 主信息在页面另一块 JSON 里
        var userJson = JsonExtractor.ExtractProperty(html, "user");
        if (userJson is not null)
        {
            try
            {
                var user = JsonNode.Parse(userJson);
                detail.AuthorName = ReadString(user?["name"]) ?? string.Empty;
                detail.AuthorId = ReadString(user?["href"]) ?? ReadString(user?["id"]) ?? string.Empty;
                detail.AuthorAvatar = ReadString(user?["headUrl"]) ?? string.Empty;
                detail.AuthorFans = ReadLong(user?["fanCountValue"]);
            }
            catch
            {
                // 用户块解析失败不影响播放
            }
        }

        // 分P 列表
        if (obj["videoList"] is JsonArray parts)
        {
            foreach (var part in parts)
            {
                if (part is null) continue;
                var videoId = ReadString(part["id"]);
                if (string.IsNullOrWhiteSpace(videoId)) continue;

                detail.Parts.Add(new VideoPart
                {
                    VideoId = videoId!,
                    Title = ReadString(part["title"]) ?? $"P{detail.Parts.Count + 1}",
                    DurationMillis = ReadLong(part["durationMillis"]),
                    DanmakuCount = (int)ReadLong(part["danmakuCount"])
                });
            }
        }

        if (string.IsNullOrEmpty(detail.CurrentVideoId) && detail.Parts.Count > 0)
        {
            detail.CurrentVideoId = detail.Parts[0].VideoId;
        }

        return detail;
    }

    internal static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        var raw = node.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim('"');
    }

    internal static long ReadLong(JsonNode? node)
    {
        if (node is null) return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out long l)) return l;
            if (value.TryGetValue<double>(out double d)) return (long)d;
        }
        return long.TryParse(node.ToString().Trim('"'), out long parsed) ? parsed : 0;
    }

    /// <summary>播放量格式化：1.2万 / 3.4亿</summary>
    public static string FormatCount(long value)
    {
        if (value >= 100_000_000) return $"{value / 100_000_000.0:0.#}亿";
        if (value >= 10_000) return $"{value / 10_000.0:0.#}万";
        return value.ToString();
    }
}
