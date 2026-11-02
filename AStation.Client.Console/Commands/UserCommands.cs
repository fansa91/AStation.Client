using AStation_Client.Services;

namespace AStation_Client.Cli.Commands;

/// <summary>
/// 账号相关命令：公开资料 / 收藏 / 播放历史。
///
/// <c>profile</c> 是匿名接口，随时可跑，用来核对 profile 那 30 个键；
/// <c>favorites</c> 和 <c>history</c> 需要登录态——先跑 <c>login</c> 把 Cookie 落盘，
/// 之后这两个命令会自动带上。
///
/// 例：
///   profile 16517519
///   favorites --type douga --count 20
///   history --page 1 --size 20
/// </summary>
public static class UserCommands
{
    /// <summary><c>profile &lt;userId&gt;</c>：用户公开资料（匿名可调）</summary>
    public static async Task<int> ProfileAsync(CliArgs args)
    {
        string text = args.Require(0, "用户 uid");
        if (!long.TryParse(text, out var userId) || userId <= 0)
        {
            throw new CliUsageException($"userId 要填数字：{text}");
        }

        var profile = await ServiceLocator.UserProfile.GetProfileAsync(userId, CliContext.Token);
        if (profile is null)
        {
            Out.Warn($"没取到 uid {userId} 的资料（用户不存在或被限制）");
            return 1;
        }

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                userId = profile.UserId,
                name = profile.Name,
                avatar = profile.Avatar,
                contentCount = profile.ContentCount,
                likeCount = profile.LikeCount,
                following = profile.Following,
                followed = profile.Followed,
                verifiedText = profile.VerifiedText,
                ipLocation = profile.IpLocation,
                registerTimeMs = profile.RegisterTimeMs,
                shareUrl = profile.ShareUrl
            });
            return 0;
        }

        Out.Line(profile.Name + (string.IsNullOrWhiteSpace(profile.VerifiedText) ? "" : $"（{profile.VerifiedText}）"));
        Out.Dim(profile.DetailText);
        Out.Line(profile.StatsText);
        return 0;
    }

    /// <summary><c>favorites [--type douga|bangumi|album|article]</c>：收藏列表（需登录）</summary>
    public static async Task<int> FavoritesAsync(CliArgs args)
    {
        var kind = ParseKind(args.Get("type") ?? args.Get("t"));
        string cursor = args.Get("cursor") ?? "0";
        int count = args.GetInt("count", 20);

        var page = await ServiceLocator.Favorite.GetFavoritesAsync(kind, cursor, count, CliContext.Token);

        if (page.Result == -401)
        {
            Out.Warn("需要登录：先跑 login（或用 whoami 确认 Cookie 还在）");
            return 1;
        }

        if (page.Result != 0)
        {
            Out.Warn($"收藏没取到：result={page.Result} {page.ErrorMessage}");
            return 1;
        }

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                kind = kind.ToString().ToLowerInvariant(),
                cursor = page.Cursor,
                hasMore = page.HasMore,
                count = page.Items.Count,
                items = page.Items.Select(i => new
                {
                    contentId = i.ContentId,
                    title = i.Title,
                    up = i.Up,
                    cover = i.Cover,
                    duration = i.DurationText,
                    viewCount = i.ViewCount,
                    updateTimeMs = i.UpdateTimeMs
                })
            });
            return 0;
        }

        if (page.Items.Count == 0)
        {
            Out.Warn("这个分类下没有收藏");
            return 0;
        }

        foreach (var item in page.Items)
        {
            Out.Line($"{item.Title}  [{item.ContentId}]");
            var meta = item.MetaText;
            if (!string.IsNullOrWhiteSpace(item.DurationText)) meta += $" · {item.DurationText}";
            if (!string.IsNullOrWhiteSpace(item.UpdateText)) meta += $" · {item.UpdateText}";
            Out.Dim("  " + meta);
        }

        Out.Line();
        Out.Ok($"共 {page.Items.Count} 条" + (page.HasMore ? $" / 下一页 cursor={page.Cursor}" : " / 没有更多"));
        return 0;
    }

    /// <summary><c>history [--page 1] [--size 20]</c>：播放历史（需登录）</summary>
    public static async Task<int> HistoryAsync(CliArgs args)
    {
        int pageNo = args.GetInt("page", 1);
        int size = args.GetInt("size", 20);
        bool withBangumi = !args.Has("no-bangumi");

        var page = await ServiceLocator.History.GetHistoryAsync(pageNo, size, withBangumi, CliContext.Token);

        if (page.Result == -401)
        {
            Out.Warn("需要登录：先跑 login（或用 whoami 确认 Cookie 还在）");
            return 1;
        }

        if (page.Result != 0)
        {
            Out.Warn($"历史没取到：result={page.Result} {page.ErrorMessage}");
            return 1;
        }

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                pageNo,
                size,
                totalCount = page.TotalCount,
                count = page.Items.Count,
                items = page.Items.Select(i => new
                {
                    resourceId = i.ResourceId,
                    videoId = i.VideoId,
                    resourceType = i.ResourceType,
                    title = i.Title,
                    up = i.Up,
                    cover = i.Cover,
                    duration = i.DurationText,
                    progress = i.ProgressText,
                    progressPercent = i.ProgressPercent,
                    browseTime = i.BrowseTimeText
                })
            });
            return 0;
        }

        if (page.Items.Count == 0)
        {
            Out.Warn("没有播放记录");
            return 0;
        }

        foreach (var item in page.Items)
        {
            Out.Line($"{item.Title}  [{item.ResourceId}]");
            var meta = item.MetaText;
            if (!string.IsNullOrWhiteSpace(item.DurationText)) meta += $" · {item.DurationText}";
            if (!string.IsNullOrWhiteSpace(item.ProgressText)) meta += $" · {item.ProgressText}";
            if (!string.IsNullOrWhiteSpace(item.BrowseTimeText)) meta += $" · {item.BrowseTimeText}";
            Out.Dim("  " + meta);
        }

        Out.Line();
        Out.Ok($"第 {pageNo} 页 {page.Items.Count} 条 / 共 {page.TotalCount} 条");
        return 0;
    }

    private static FavoriteKind ParseKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "bangumi" or "bgm" or "番剧" => FavoriteKind.Bangumi,
        "album" or "专辑" => FavoriteKind.Album,
        "article" or "文章" => FavoriteKind.Article,
        _ => FavoriteKind.Douga
    };
}
