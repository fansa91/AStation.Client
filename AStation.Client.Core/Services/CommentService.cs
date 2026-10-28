using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 评论服务。
/// 接口：GET /rest/pc-direct/comment/list
///   参数 sourceId = dougaId / bangumiId，sourceType = 3（视频）；番剧用 2。
///   另有 pivotCommentId / newPivotCommentId（首页填 0）、t（毫秒时间戳防缓存）、supportZtEmot=true。
///   每页固定 50 条（pageSize 不可配）。
/// 响应：stickyComments 置顶 + hotComments 热门 + rootComments 主楼 + subCommentsMap 楼中楼。
///
/// 坑：这里的字段类型不稳定（likeCount 可能是数字也可能是字符串，commentId 实测是数字），
/// 一律按 JsonNode 容错读取，不要用强类型 DTO —— 弹幕接口就是被 color 的 int/string 差异
/// 导致整张列表反序列化失败。
/// </summary>
public class CommentService
{
    private readonly ApiService _api;

    public CommentService(ApiService api)
    {
        _api = api;
    }

    internal ApiService GetApiForTest() => _api;

    /// <summary>
    /// 拉一页评论。番剧传 sourceType=2。
    /// </summary>
    public async Task<CommentResult> GetCommentsAsync(
        string sourceId,
        int sourceType = 3,
        int page = 1,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["sourceId"] = sourceId,
            ["sourceType"] = sourceType.ToString(),
            ["page"] = page.ToString(),
            ["pivotCommentId"] = "0",
            ["newPivotCommentId"] = "0",
            ["supportZtEmot"] = "true",
            ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        };

        try
        {
            var response = await _api.GetAsync<JsonNode>("/rest/pc-direct/comment/list", query, ct);

            if (response is not JsonObject obj)
            {
                return new CommentResult { ErrorMessage = "评论接口无响应", Page = page };
            }

            int code = ReadInt(obj["result"]);
            if (code != 0)
            {
                return new CommentResult
                {
                    ErrorMessage = ReadString(obj["error_msg"]) ?? $"评论拉取失败（result={code}）",
                    Page = page
                };
            }

            return ParseComments(obj, page);
        }
        catch (Exception ex)
        {
            return new CommentResult { ErrorMessage = $"评论拉取失败：{ex.Message}", Page = page };
        }
    }

    /// <summary>解析评论列表响应</summary>
    internal static CommentResult ParseComments(JsonObject obj, int page)
    {
        var result = new CommentResult
        {
            Page = page,
            TotalCount = ReadLong(obj["commentCount"]),
            TotalPage = ReadInt(obj["totalPage"])
        };

        var repliesByRoot = ParseSubComments(obj["subCommentsMap"]);
        var seen = new HashSet<long>();

        // 置顶 → 热门 → 普通，按这个顺序合并并去重
        foreach (var arrayName in new[] { "stickyComments", "hotComments", "rootComments" })
        {
            if (obj[arrayName] is not JsonArray array) continue;

            foreach (var node in array)
            {
                if (node is not JsonObject comment) continue;

                var item = ParseComment(comment);
                if (item is null) continue;
                if (!seen.Add(item.CommentId)) continue;

                // 楼中楼随主楼一起带过来
                if (repliesByRoot.TryGetValue(item.CommentId, out var replies))
                {
                    item.Replies.AddRange(replies);
                }

                result.Items.Add(item);
            }
        }

        if (result.Items.Count > 0 && result.TotalPage <= 0) result.TotalPage = page;

        return result;
    }

    /// <summary>
    /// 解析楼中楼：结构是 { "&lt;主楼 commentId&gt;": { "pcursor": "...", "subComments": [ ... ] } }。
    /// 每个主楼固定只下发前 3 条（pageSize 等参数不影响），所以这里不做分页。
    /// </summary>
    internal static Dictionary<long, List<CommentItem>> ParseSubComments(JsonNode? node)
    {
        var map = new Dictionary<long, List<CommentItem>>();
        if (node is not JsonObject obj) return map;

        foreach (var (key, box) in obj)
        {
            if (!long.TryParse(key, out long rootId)) continue;
            if (box?["subComments"] is not JsonArray array) continue;

            var list = new List<CommentItem>();
            foreach (var child in array)
            {
                if (child is not JsonObject comment) continue;

                var item = ParseComment(comment);
                if (item is null) continue;

                item.IsReply = true;
                list.Add(item);
            }

            if (list.Count > 0) map[rootId] = list;
        }

        return map;
    }

    private static CommentItem? ParseComment(JsonObject comment)
    {
        var content = ReadString(comment["content"]);
        var userName = ReadString(comment["userName"]);

        // 图片评论 content 可能为空，但完全没内容又没用户名的丢弃
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(userName)) return null;

        return new CommentItem
        {
            CommentId = ReadLong(comment["commentId"]),
            UserId = ReadString(comment["userId"]) ?? string.Empty,
            UserName = string.IsNullOrWhiteSpace(userName) ? "匿名用户" : userName!,
            Avatar = ReadAvatar(comment),
            Content = content ?? string.Empty,
            LikeCount = ReadInt(comment["likeCount"]),
            PostDate = ReadString(comment["postDate"]) ?? string.Empty,
            TimestampMillis = ReadLong(comment["timestamp"]),
            SubCommentCount = ReadInt(comment["subCommentCount"]),
            IsUp = ReadBool(comment["isUp"]),
            IsSticky = ReadBool(comment["isSticky"]),
            Floor = ReadInt(comment["floor"]),
            ReplyToUserName = ReadString(comment["replyToUserName"]) ?? string.Empty
        };
    }

    /// <summary>
    /// 头像有两个来源：headUrl 是 [{cdn,url}] 数组，userHeadImgInfo.thumbnailImage.cdnUrls 也是数组。
    /// </summary>
    private static string ReadAvatar(JsonObject comment)
    {
        if (comment["headUrl"] is JsonArray headUrls)
        {
            foreach (var node in headUrls)
            {
                var url = node is JsonObject o ? ReadString(o["url"]) : ReadString(node);
                if (!string.IsNullOrWhiteSpace(url)) return url!;
            }
        }

        var direct = ReadString(comment["avatarImage"]) ?? ReadString(comment["userHeadImg"]);
        if (!string.IsNullOrWhiteSpace(direct)) return direct!;

        var thumb = comment["userHeadImgInfo"]?["thumbnailImage"];
        if (thumb?["cdnUrls"] is JsonArray cdns)
        {
            foreach (var node in cdns)
            {
                var url = node is JsonObject o ? ReadString(o["url"]) : ReadString(node);
                if (!string.IsNullOrWhiteSpace(url)) return url!;
            }
        }

        return string.Empty;
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

    private static bool ReadBool(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out bool b)) return b;
            if (value.TryGetValue<int>(out int i)) return i != 0;
        }
        return false;
    }
}
