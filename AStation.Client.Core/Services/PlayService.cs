using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AStation_Client.Services;

/// <summary>
/// 视频播放服务。
/// 对应接口：GET /rest/pc-direct/play/playInfo/ksPlayJson
///
/// 返回结构（实测）：
/// { "result":0, "playInfo": { "ksPlayJson": "&lt;字符串化的 JSON&gt;", "ksPlayJsonHevc": "&lt;字符串化的 JSON&gt;",
///                             "duration":307563, "durationMillis":307000, "userPlayedSeconds":0, ... } }
/// 注意 ksPlayJson / ksPlayJsonHevc 的值是「字符串里再包一层 JSON」，必须二次解析。
///
/// 取流参数：resourceId = dougaId（视频）/ bangumiId（番剧），resourceType：2=视频 1=番剧。
/// 切集/切分P 时带 videoId（该集的 videoId）+ mkey（番剧页下发的播放授权 key）。
/// </summary>
public class PlayService
{
    private readonly ApiService _api;

    public PlayService(ApiService api)
    {
        _api = api;
    }

    internal ApiService GetApiForTest() => _api;

    /// <summary>
    /// 获取播放信息。
    /// </summary>
    /// <param name="resourceId">视频填 dougaId，番剧填 bangumiId</param>
    /// <param name="resourceType">2=视频，1=番剧</param>
    /// <param name="videoId">切分P/切集时的 videoId（不是 itemId）</param>
    /// <param name="mkey">番剧播放授权 key</param>
    public async Task<PlayInfoResponse?> GetPlayInfoAsync(
        string resourceId,
        int resourceType = 2,
        string? videoId = null,
        string? mkey = null,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["resourceId"] = resourceId,
            ["resourceType"] = resourceType.ToString()
        };

        if (!string.IsNullOrWhiteSpace(videoId)) query["videoId"] = videoId!;
        if (!string.IsNullOrWhiteSpace(mkey)) query["mkey"] = mkey!;

        return await _api.GetAsync<PlayInfoResponse>("/rest/pc-direct/play/playInfo/ksPlayJson", query, ct);
    }

    /// <summary>
    /// 把接口响应整理成「清晰度列表 + 续播进度」。
    /// </summary>
    /// <param name="preferHevc">优先 H265（ksPlayJsonHevc）；关掉则优先 H264</param>
    public PlayStreamResult BuildPlayResult(PlayInfoResponse? response, bool preferHevc = true)
    {
        var result = new PlayStreamResult();
        var data = response?.PlayInfo;

        if (data is null)
        {
            result.ErrorMessage = DescribeError(response?.Result);
            return result;
        }

        result.DurationSeconds = data.Duration > 0 ? data.Duration / 1000.0 : data.DurationMillis / 1000.0;
        result.UserPlayedSeconds = data.UserPlayedSeconds;

        var hevc = ParseQualities(data.KsPlayJsonHevc, isHevc: true);
        var h264 = ParseQualities(data.KsPlayJson, isHevc: false);

        var primary = preferHevc
            ? (hevc.Count > 0 ? hevc : h264)
            : (h264.Count > 0 ? h264 : hevc);

        result.IsHevc = primary.Count > 0 && primary[0].IsHevc;

        // 按码率从高到低排列，最高清排第一
        foreach (var item in primary.OrderByDescending(q => q.Height).ThenByDescending(q => q.Bitrate))
        {
            result.Qualities.Add(item);
        }

        if (result.Qualities.Count == 0)
        {
            result.ErrorMessage = DescribeError(response?.Result);
        }

        return result;
    }

    /// <summary>
    /// 取最高清晰度地址（兼容旧调用）。
    /// </summary>
    public string? ExtractBestStreamUrl(PlayInfoResponse? playInfo)
        => BuildPlayResult(playInfo).Best?.Url;

    /// <summary>
    /// 把错误码翻译成人话（错误码取自 ACFUN_API.md 番剧与错误码速查表）
    /// </summary>
    public static string DescribeError(int? result) => result switch
    {
        101104 => "该内容有地域限制，当前网络无法观看",
        131004 => "需要付费或大会员才能观看",
        101103 => "资源 id 无效（番剧请传 bangumiId，不是 itemId）",
        101100 => "视频异常，暂时无法播放",
        21 => "参数格式错误",
        11 => "网络错误，请稍后重试",
        null or 0 => "暂时拿不到播放地址，请稍后重试",
        _ => $"取流失败（result={result}）"
    };

    /// <summary>
    /// 解析 ksPlayJson / ksPlayJsonHevc（值可能是字符串化的 JSON，也可能是对象）
    /// </summary>
    internal static List<PlayQuality> ParseQualities(JsonNode? node, bool isHevc)
    {
        var list = new List<PlayQuality>();

        var root = Unwrap(node);
        if (root is not JsonObject obj) return list;

        if (obj["adaptationSet"] is not JsonArray adaptationSet) return list;

        foreach (var set in adaptationSet)
        {
            if (set?["representation"] is not JsonArray representations) continue;

            foreach (var rep in representations)
            {
                if (rep is null) continue;

                var url = ReadString(rep["url"]);
                if (string.IsNullOrWhiteSpace(url)) continue;

                // backupUrl 是数组，可能有多条（阿里云 / 金山云…），全部收进来当 CDN 备选
                var backups = rep["backupUrl"] is JsonArray array
                    ? array.Select(ReadString).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!).ToList()
                    : new List<string>();

                var quality = new PlayQuality
                {
                    Label = ReadString(rep["qualityLabel"]) ?? "未知",
                    Url = url!,
                    BackupUrl = backups.FirstOrDefault(),
                    Width = ReadInt(rep["width"]),
                    Height = ReadInt(rep["height"]),
                    Bitrate = ReadInt(rep["maxBitrate"]),
                    IsHevc = isHevc
                };

                quality.CdnSources.Add(BuildCdn(url!, isBackup: false));
                foreach (var backup in backups)
                {
                    quality.CdnSources.Add(BuildCdn(backup, isBackup: true));
                }

                list.Add(quality);
            }
        }

        return list;
    }

    /// <summary>把一条地址包装成 CDN 线路（按 host 给出可读名称）</summary>
    internal static CdnSource BuildCdn(string url, bool isBackup)
    {
        string host = string.Empty;
        try
        {
            host = new Uri(url).Host;
        }
        catch
        {
            // 地址异常时不影响播放，退化成空 host
        }

        return new CdnSource
        {
            Url = url,
            Host = host,
            IsBackup = isBackup,
            Name = DescribeCdn(host)
        };
    }

    /// <summary>
    /// 按 CDN 域名给出中文名称。AcFun 实测下发的：
    /// tx-video.acfun.cn（腾讯云）/ ali-video.acfun.cn（阿里云），其余按前缀兜底。
    /// </summary>
    internal static string DescribeCdn(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "未知线路";

        var h = host.ToLowerInvariant();

        if (h.Contains("tx-video") || h.Contains("tencent")) return "腾讯云";
        if (h.Contains("ali-video") || h.Contains("aliyun")) return "阿里云";
        if (h.Contains("ks-video") || h.Contains("ksyun") || h.Contains("ks3")) return "金山云";
        if (h.Contains("hw-video") || h.Contains("huawei")) return "华为云";
        if (h.Contains("ws-video") || h.Contains("wangsu")) return "网宿";
        if (h.Contains("chinaunicom") || h.Contains("cu-video")) return "联通";

        // 兜底：取域名第一段
        int dot = h.IndexOf('.');
        return dot > 0 ? h[..dot] : h;
    }

    /// <summary>把「字符串化的 JSON」解成节点</summary>
    private static JsonNode? Unwrap(JsonNode? node)
    {
        try
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            return node;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        var raw = node.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim('"');
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is null) return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out int i)) return i;
            if (value.TryGetValue<double>(out double d)) return (int)d;
        }
        return int.TryParse(node.ToString().Trim('"'), out int parsed) ? parsed : 0;
    }
}

/// <summary>
/// ksPlayJson 接口响应（外层信封 + playInfo）
/// </summary>
public class PlayInfoResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("playInfo")]
    public PlayInfoData? PlayInfo { get; set; }

    [JsonPropertyName("error_msg")]
    public string? ErrorMsg { get; set; }
}

/// <summary>
/// playInfo 内部结构（参考 ACFUN_API.md 2.1 节）
/// </summary>
public class PlayInfoData
{
    /// <summary>H264 清单，值是「字符串化的 JSON」</summary>
    [JsonPropertyName("ksPlayJson")]
    public JsonNode? KsPlayJson { get; set; }

    /// <summary>H265 清单，值是「字符串化的 JSON」</summary>
    [JsonPropertyName("ksPlayJsonHevc")]
    public JsonNode? KsPlayJsonHevc { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("durationMillis")]
    public long DurationMillis { get; set; }

    /// <summary>服务端记录的续播进度（秒，登录态下发）</summary>
    [JsonPropertyName("userPlayedSeconds")]
    public long UserPlayedSeconds { get; set; }

    [JsonPropertyName("firstFrameUrls")]
    public List<string>? FirstFrameUrls { get; set; }
}
