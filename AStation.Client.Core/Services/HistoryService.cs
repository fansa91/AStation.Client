using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 播放历史（服务端，需登录）。
///
/// 接口：POST /rest/pc-direct/browse/history/list
///   form: pageNo / pageSize / resourceTypes（Web 端传空串即「全部类型」）
///   响应: { pageSize, histories[], totalCount, pageNo }
///   **注意：成功的响应顶层没有 result 字段**（ACFUN_API.md 2026-11-02 实锤），
///   因此 Parse 里 result 缺失不能判失败——只要 histories 是数组就算拿到数据。
///   未登录: HTTP 401 + {"result":-401,"error_msg":"user not login"}
///
/// histories[] 关键字段：
///   resourceId（ac 号）/ videoId（当前分P）/ resourceType / title / cover /
///   durationSecondsShow（"07:26"）/ playedSecondsShow（"已看完"）/ durationSeconds / playedSeconds /
///   browseTime（毫秒时间戳）/ dougaVideoTitle（分P名）/ bangumiItemEpisodeName（番剧集名）/ user.name
///
/// 服务端历史由 App 端 protobuf 上报通道（report/playContent + log/action）自动写入，
/// 客户端正常取流即可积累记录，这里只负责读。
/// </summary>
public class HistoryService
{
    private const string Path = "/rest/pc-direct/browse/history/list";

    private readonly ApiService _api;

    public HistoryService(ApiService api)
    {
        _api = api;
    }

    internal ApiService GetApiForTest() => _api;

    /// <summary>
    /// 取一页播放历史。<paramref name="includeBangumi"/> 为 true 时按官方 Web 端标准传空串
    ///（空串 = 全部类型，番剧 + 视频都在内）；为 false 时只取视频（"2"）。
    /// </summary>
    public async Task<HistoryPage> GetHistoryAsync(
        int pageNo = 1,
        int pageSize = 20,
        bool includeBangumi = true,
        CancellationToken ct = default)
    {
        // Web 端 resourceTypes 是单值：空串 = 全部类型，"2" = 只视频（ACFUN_API.md 实锤）。
        // 旧版误传重复键 1/2，与文档不符；改为空串，顺带覆盖了文章等其他类型。
        var form = new List<KeyValuePair<string, string>>
        {
            new("pageNo", Math.Max(1, pageNo).ToString()),
            new("pageSize", Math.Max(1, pageSize).ToString()),
            new("resourceTypes", includeBangumi ? string.Empty : "2")
        };

        try
        {
            var response = await _api.PostFormAllowErrorAsync<JsonNode>(Path, form, ct);
            var page = Parse(response);

            // -401 是确定的「没登录」，直接返回；其余非 0 才尝试退一步。
            if (page.Result == 0 || page.Result == -401)
            {
                return page;
            }

            // 兜底：全部类型取失败时，退到「只视频」再试一次。
            if (includeBangumi)
            {
                return await GetHistoryAsync(pageNo, pageSize, includeBangumi: false, ct);
            }

            return page;
        }
        catch (Exception ex)
        {
            return new HistoryPage { Result = -1, ErrorMessage = ex.Message };
        }
    }

    internal HistoryPage Parse(JsonNode? node)
    {
        var page = new HistoryPage();
        if (node is not JsonObject root) return page;

        // Web 端成功响应没有 result 字段（顶层 {pageSize, histories, totalCount, pageNo}），
        // 缺失时按「拿到了数据就算成功」处理；只有显式写了非 0 的 result 才算失败。
        if (root["result"] is not null && root["result"].GetValue<int>() is int result && result != 0)
        {
            page.Result = result;
        }
        else
        {
            page.Result = root["histories"] is JsonArray ? 0 : -1;
        }
        page.ErrorMessage = root["error_msg"]?.GetValue<string>();
        page.TotalCount = root["totalCount"]?.GetValue<int>() ?? 0;

        if (root["histories"] is not JsonArray list) return page;

        foreach (var entry in list)
        {
            if (entry is not JsonObject item) continue;

            int played = ReadInt(item, "playedSeconds");
            int duration = ReadInt(item, "durationSeconds");

            var history = new HistoryItem
            {
                ResourceId = ReadString(item, "resourceId"),
                VideoId = ReadString(item, "videoId"),
                ResourceType = ReadInt(item, "resourceType") is var type && type > 0 ? type : 2,
                Title = ReadString(item, "title"),
                Cover = ReadString(item, "cover"),
                DurationText = ReadString(item, "durationSecondsShow"),
                ProgressText = ReadString(item, "playedSecondsShow"),
                BrowseTimeText = FormatBrowseTime(ReadLong(item, "browseTime")),
                Subtitle = FirstNonEmpty(
                    ReadString(item, "dougaVideoTitle"),
                    ReadString(item, "bangumiItemEpisodeName")),
                Up = ReadString(item["user"], "name")
            };

            if (duration > 0)
            {
                history.ProgressPercent = Math.Clamp(played * 100.0 / duration, 0, 100);
            }
            else if (history.ProgressText == "已看完")
            {
                history.ProgressPercent = 100;
            }

            if (string.IsNullOrWhiteSpace(history.Title)) continue;
            page.Items.Add(history);
        }

        return page;
    }

    private static string ReadString(JsonNode? node, string name)
    {
        try
        {
            var value = node?[name];
            return value is null ? string.Empty : (value.GetValue<object>()?.ToString() ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ReadInt(JsonNode? node, string name)
    {
        var text = ReadString(node, name);
        return int.TryParse(text, out var value) ? value : 0;
    }

    private static long ReadLong(JsonNode? node, string name)
    {
        var text = ReadString(node, name);
        return long.TryParse(text, out var value) ? value : 0;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// <summary>毫秒时间戳 → 「今天 14:30」「昨天 21:05」「10月28日」</summary>
    internal static string FormatBrowseTime(long millis)
    {
        if (millis <= 0) return string.Empty;

        DateTime local;
        try
        {
            local = DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime;
        }
        catch
        {
            return string.Empty;
        }

        var today = DateTime.Today;
        if (local.Date == today) return $"今天 {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        if (local.Year == today.Year) return local.ToString("M月d日");
        return local.ToString("yyyy年M月d日");
    }
}
