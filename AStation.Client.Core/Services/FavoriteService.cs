using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 收藏列表（需登录）。
///
/// 接口走 **App 基址** api-new.app.acfun.cn，四个分类各一条：
///   POST /rest/app/favorite/{dougaList | bangumiList | albumList | articleList}
///   form: cursor / count
///   响应: { result, cursor, favoriteList[FavoriteModel] }
///   未登录: HTTP 401 + {"result":-401,"error_msg":"token value error"}
///
/// 两个坑：
/// 1. 首屏 <c>cursor</c> 必须传 <c>"0"</c>，传空串会被判 <c>result=21 参数格式错误</c>；
/// 2. 网页端那套 <c>/rest/pc-direct/favorite/*</c> 是**夹内**列表（要 folderId），
///    与这里不是一个东西。只在 App 基址失败时作为兜底再试一次。
///
/// FavoriteModel 字段（jadx 实锤）：
///   contentId / contentTitle / contentDesc / contentImg / coverImage /
///   albumId / albumTitle / albumDesc / groupId / views / stows / comments /
///   updateTime / userId / userName / userImg
/// 注意图片字段有两个名字，不同分类给的不一样，解析时按序兜。
/// </summary>
public class FavoriteService
{
    private const string AppHost = "https://api-new.app.acfun.cn";
    private const string WebHost = "https://www.acfun.cn";

    private readonly ApiService _api;

    public FavoriteService(ApiService api)
    {
        _api = api;
    }

    internal ApiService GetApiForTest() => _api;

    /// <summary>分类 → 接口路径尾（App 端与 Web 端同名）</summary>
    internal static string SlugOf(FavoriteKind kind) => kind switch
    {
        FavoriteKind.Bangumi => "bangumiList",
        FavoriteKind.Album => "albumList",
        FavoriteKind.Article => "articleList",
        _ => "dougaList"
    };

    /// <summary>
    /// 取一页收藏。<paramref name="cursor"/> 首屏传 <c>"0"</c>，之后用上一页返回的 cursor。
    /// </summary>
    public async Task<FavoritePage> GetFavoritesAsync(
        FavoriteKind kind,
        string cursor = "0",
        int count = 20,
        CancellationToken ct = default)
    {
        var slug = SlugOf(kind);
        var form = new Dictionary<string, string>
        {
            ["cursor"] = string.IsNullOrWhiteSpace(cursor) ? "0" : cursor,
            ["count"] = Math.Clamp(count, 1, 50).ToString()
        };

        try
        {
            // 主路：App 基址
            var node = await _api.PostFormToHostAllowErrorAsync<JsonNode>(
                AppHost, $"/rest/app/favorite/{slug}", form, ct: ct);
            var page = Parse(node, kind);

            // -401 是确定的「没登录」，没必要再试；其余失败走网页端兜底
            if (page.Result != 0 && page.Result != -401)
            {
                var fallback = await _api.PostFormToHostAllowErrorAsync<JsonNode>(
                    WebHost, $"/rest/pc-direct/favorite/{slug}", form, ct: ct);
                var retry = Parse(fallback, kind);
                if (retry.Result == 0) return retry;
            }

            return page;
        }
        catch (Exception ex)
        {
            return new FavoritePage { Result = -1, ErrorMessage = ex.Message };
        }
    }

    /// <summary>收藏夹（文件夹）列表，用于将来做夹内浏览；未登录同样 -401。</summary>
    public async Task<FavoritePage> GetFoldersAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await _api.PostFormToHostAllowErrorAsync<JsonNode>(
                AppHost, "/rest/app/favorite/folder/list", new Dictionary<string, string>(), ct: ct);
            return Parse(node, FavoriteKind.Douga);
        }
        catch (Exception ex)
        {
            return new FavoritePage { Result = -1, ErrorMessage = ex.Message };
        }
    }

    internal FavoritePage Parse(JsonNode? node, FavoriteKind kind)
    {
        var page = new FavoritePage { Result = -1 };
        if (node is not JsonObject root) return page;

        page.Result = ReadInt(root, "result", -1);
        page.ErrorMessage = ReadString(root, "error_msg") is { Length: > 0 } msg ? msg : null;
        page.Cursor = ReadString(root, "cursor");

        var list = FindList(root);
        if (list is null) return page;

        foreach (var entry in list)
        {
            if (entry is not JsonObject item) continue;

            var favorite = new FavoriteItem
            {
                Kind = kind,
                ContentId = FirstNonEmpty(
                    ReadString(item, "contentId"),
                    ReadString(item, "acId"),
                    ReadString(item, "resourceId"),
                    ReadString(item, "id")),
                Title = FirstNonEmpty(
                    ReadString(item, "contentTitle"),
                    ReadString(item, "albumTitle"),
                    ReadString(item, "title")),
                Cover = FirstNonEmpty(
                    ReadString(item, "contentImg"),
                    ReadString(item, "coverImage"),
                    ReadString(item, "cover"),
                    ReadString(item, "img")),
                Up = FirstNonEmpty(
                    ReadString(item, "userName"),
                    ReadString(item["user"], "name"),
                    ReadString(item["userInfo"], "name")),
                ViewCount = ReadInt(item, "views"),
                StowCount = ReadInt(item, "stows"),
                UpdateTimeMs = ReadLong(item, "updateTime") is var t && t > 0
                    ? t
                    : ReadLong(item, "lastFavoriteTime")
            };

            favorite.DurationText = FormatDuration(item);
            favorite.UpdateText = FormatUpdateTime(favorite.UpdateTimeMs);

            // 番剧/专辑没标题时用另一组字段兜一下
            if (string.IsNullOrWhiteSpace(favorite.Title))
            {
                favorite.Title = FirstNonEmpty(
                    ReadString(item, "albumTitle"),
                    ReadString(item, "contentDesc"));
            }

            if (string.IsNullOrWhiteSpace(favorite.Title)) continue;
            page.Items.Add(favorite);
        }

        return page;
    }

    /// <summary>响应里列表字段名在不同分类/端上不一致，按序找第一个数组</summary>
    private static JsonArray? FindList(JsonObject root)
    {
        string[] keys = ["favoriteList", "dataList", "data", "list", "items"];
        foreach (var key in keys)
        {
            if (root[key] is JsonArray array) return array;
            // 有些端把列表包一层 { data: { favoriteList: [...] } }
            if (root[key] is JsonObject nested && FindList(nested) is { } inner) return inner;
        }

        return null;
    }

    /// <summary>duration（秒，数字或字符串）/ durationSecondsShow / durationShow → "mm:ss"</summary>
    private static string FormatDuration(JsonObject item)
    {
        var text = FirstNonEmpty(
            ReadString(item, "durationShow"),
            ReadString(item, "durationSecondsShow"));
        if (!string.IsNullOrWhiteSpace(text)) return text;

        var seconds = ReadInt(item, "duration");
        if (seconds <= 0) seconds = ReadInt(item, "durationSeconds");
        if (seconds <= 0) return string.Empty;

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>收藏时间 → 「刚刚 / 12 分钟前 / 3 天前 / 10月28日 / 2025年10月28日」</summary>
    internal static string FormatUpdateTime(long millis)
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

        var delta = DateTime.Now - local;
        if (delta.TotalMinutes < 1) return "刚刚";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours} 小时前";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} 天前";

        return local.Year == DateTime.Today.Year
            ? local.ToString("M月d日")
            : local.ToString("yyyy年M月d日");
    }

    private static string ReadString(JsonNode? node, string name)
    {
        try
        {
            var value = node?[name];
            if (value is null) return string.Empty;
            return value.GetValue<object>()?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ReadInt(JsonNode? node, string name, int fallback = 0)
    {
        var text = ReadString(node, name);
        return int.TryParse(text, out var value) ? value : fallback;
    }

    private static long ReadLong(JsonNode? node, string name)
    {
        var text = ReadString(node, name);
        return long.TryParse(text, out var value) ? value : 0;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
