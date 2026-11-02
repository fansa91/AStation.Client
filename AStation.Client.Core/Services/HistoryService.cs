using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 播放历史（服务端，需登录）。
///
/// 接口：POST /rest/pc-direct/browse/history/list
///   form: pageNo / pageSize / resourceTypes（可重复，1 = 番剧、2 = 视频）
///   响应: { result, pageNo, pageSize, totalCount, histories[] }
///   未登录: HTTP 401 + {"result":-401,"error_msg":"user not login"}
///
/// histories[] 关键字段：
///   resourceId（ac 号）/ videoId（当前分P）/ resourceType / title / cover /
///   durationSecondsShow（"07:26"）/ playedSecondsShow（"已看完"）/ durationSeconds / playedSeconds /
///   browseTime（毫秒时间戳）/ dougaVideoTitle（分P名）/ bangumiItemEpisodeName（番剧集名）/ user.name
///
/// 注意 resourceTypes 在官方文档里是**重复键**（1 和 2 各来一次），
/// Dictionary 版 PostForm 会去重，所以走支持重复键的那个重载。
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
    /// 取一页播放历史。<paramref name="includeBangumi"/> 同时要番剧（resourceTypes 1 + 2）。
    /// </summary>
    public async Task<HistoryPage> GetHistoryAsync(
        int pageNo = 1,
        int pageSize = 20,
        bool includeBangumi = true,
        CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("pageNo", Math.Max(1, pageNo).ToString()),
            new("pageSize", Math.Max(1, pageSize).ToString())
        };
        if (includeBangumi)
        {
            form.Add(new("resourceTypes", "1"));
            form.Add(new("resourceTypes", "2"));
        }

        try
        {
            var response = await _api.PostFormAsync<JsonNode>(Path, form, ct);
            var page = Parse(response);

            // resourceTypes 万一不被接受（服务端只认单值），退一步不带类型再试一次
            if (page.Result != 0 && includeBangumi)
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

        page.Result = root["result"]?.GetValue<int>() ?? -1;
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
