using AStation_Client.Services;

namespace AStation_Client.Cli.Commands;

/// <summary>
/// 番剧 / 直播 / 取流 / 表情字典。
/// </summary>
public static class MediaCommands
{
    // ───────────────────────────── bangumi ─────────────────────────────

    public static async Task<int> BangumiAsync(CliArgs args)
    {
        string first = args.Optional(0) ?? "list";

        if (first.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            int limit = Math.Clamp(args.GetInt("limit", 20), 1, 100);
            var items = await ServiceLocator.Bangumi.GetBangumiListAsync(CliContext.Token);

            if (args.Has("json"))
            {
                JsonOut.Write(new { count = items.Count, items });
                return 0;
            }

            Out.Section($"番剧库 · {items.Count} 部");
            var listRows = items.Take(limit).Select((b, i) => new[]
            {
                (i + 1).ToString(),
                b.Title,
                b.EpisodeText,
                b.StatText,
                b.BangumiId,
            }).ToList();

            Out.Table(new[] { "#", "标题", "集数", "追番", "bangumiId" }, listRows, 4, 40, 10, 9, 10);
            if (items.Count > limit) Out.Dim($"\n  只显示前 {limit} 部，用 --limit 调整");
            return 0;
        }

        var detail = await ServiceLocator.Bangumi.GetBangumiDetailAsync(first, CliContext.Token);
        if (detail is null)
        {
            Out.Error($"没有取到番剧 {first} 的详情");
            return 1;
        }

        if (args.Has("json"))
        {
            JsonOut.Write(detail);
            return 0;
        }

        Out.Section(detail.Title);
        Out.KeyValues(
            ("bangumiId", detail.BangumiId),
            ("状态", detail.UpdateStatus),
            ("集数", $"{detail.ItemCount} 集，当前 {detail.EpisodeName}"),
            ("数据", $"{detail.PlayCount} 播放 · {detail.StowCount} 追番 · {detail.LikeCount} 赞"),
            ("付费", detail.NeedPay ? "需要付费 / 大会员" : "免费"),
            ("mkey", string.IsNullOrWhiteSpace(detail.Mkey) ? "（无）" : detail.Mkey),
            ("videoId", detail.CurrentVideoId));

        Out.Line();
        Out.Dim($"剧集（{detail.Episodes.Count}）：");
        var episodeRows = detail.Episodes.Select((e, i) => new[]
        {
            (i + 1).ToString(),
            e.Name,
            e.VideoId,
            e.NeedPay ? "付费" : string.Empty,
        }).ToList();

        Out.Table(new[] { "#", "集名", "videoId", "" }, episodeRows, 4, 30, 12, 4);
        return 0;
    }

    // ───────────────────────────── live ─────────────────────────────

    public static async Task<int> LiveAsync(CliArgs args)
    {
        string first = args.Optional(0) ?? "list";

        if (first.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            string authorId = args.Require(1, "主播 uid（authorId）");
            string liveId = args.Require(2, "直播间 id（liveId）");

            var streams = await ServiceLocator.LivePlay.GetStreamsAsync(authorId, liveId, CliContext.Token);

            if (args.Has("json"))
            {
                JsonOut.Write(streams);
                return streams.HasStream ? 0 : 1;
            }

            Out.Section($"直播拉流 · {liveId}");
            if (!streams.HasStream)
            {
                Out.Error(streams.ErrorMessage ?? "没有取到拉流地址（主播可能没在播）");
                return 1;
            }

            Out.KeyValues(("流名称", streams.StreamName));
            Out.Line();
            var rows = streams.Qualities.Select(q => new[]
            {
                q.Name,
                q.BitrateText,
                q.Url,
            }).ToList();

            Out.Table(new[] { "档位", "码率", "地址" }, rows, 10, 12, 0);
            Out.Dim("\n  直播只有 FLV，系统解码器播不了，桌面端走 FFmpegInteropX。");
            return 0;
        }

        int channel = args.GetInt("channel", 0);
        int limit = Math.Clamp(args.GetInt("limit", 20), 1, 100);

        var result = await ServiceLocator.Live.GetChannelListAsync(channel, CliContext.Token);
        var items = result.Items.Count > 0
            ? result.Items
            : await ServiceLocator.Live.GetHotLiveListAsync(CliContext.Token);

        if (args.Has("json"))
        {
            JsonOut.Write(new { channel, total = result.TotalCount, items });
            return 0;
        }

        Out.Section($"直播间 · filterId={channel} · 在播 {result.TotalCount}");
        if (items.Count == 0)
        {
            Out.Warn("没有取到直播列表（分类接口不通时会退回首页 SSR，两边都空说明接口有变动）");
            return 1;
        }

        var liveRows = items.Take(limit).Select((l, i) => new[]
        {
            (i + 1).ToString(),
            l.Title,
            l.Author,
            string.IsNullOrWhiteSpace(l.OnlineText) ? l.OnlineCount.ToString() : l.OnlineText,
            l.Tag,
            l.AuthorId,
            l.LiveId,
        }).ToList();

        Out.Table(new[] { "#", "标题", "主播", "在线", "分类", "authorId", "liveId" }, liveRows, 4, 30, 14, 7, 8, 11, 12);
        Out.Dim("\n  取拉流地址：live play <authorId> <liveId>");
        return 0;
    }

    // ───────────────────────────── play ─────────────────────────────

    public static async Task<int> PlayAsync(CliArgs args)
    {
        string resourceId = args.Require(0, "resourceId（视频填 acId，番剧填 bangumiId）");
        int resourceType = args.GetInt("type", 2);
        string? videoId = args.Get("video");
        string? mkey = args.Get("mkey");

        var response = await ServiceLocator.Play.GetPlayInfoAsync(
            resourceId, resourceType, videoId, mkey, CliContext.Token);

        bool preferHevc = !args.Has("h264") && AppSettings.Current.PreferHevc;
        var result = ServiceLocator.Play.BuildPlayResult(response, preferHevc);

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                resourceId,
                resourceType,
                hevc = result.IsHevc,
                duration = result.DurationSeconds,
                error = result.ErrorMessage,
                qualities = result.Qualities.Select(q => new
                {
                    q.Label,
                    q.ResolutionText,
                    q.Bitrate,
                    q.Url,
                    cdn = q.CdnSources.Select(c => new { c.Name, c.Host, c.Url, c.IsBackup }),
                }),
            });
            return result.HasStream ? 0 : 1;
        }

        Out.Section($"取流 · resourceId={resourceId} · type={resourceType}");
        if (!result.HasStream)
        {
            Out.Error(result.ErrorMessage ?? "没有取到播放地址");
            return 1;
        }

        Out.KeyValues(
            ("时长", Out.Duration(result.DurationSeconds)),
            ("编码", result.IsHevc ? "H.265（ksPlayJsonHevc）" : "H.264（ksPlayJson）"),
            ("续播", result.UserPlayedSeconds > 0 ? Out.Duration(result.UserPlayedSeconds) : "（未登录 / 无记录）"));

        Out.Line();
        var rows = result.Qualities.Select(q => new[]
        {
            q.Label,
            q.ResolutionText,
            q.Bitrate > 0 ? $"{q.Bitrate} kbps" : "-",
            q.CdnSources.Count.ToString(),
            q.Url,
        }).ToList();

        Out.Table(new[] { "清晰度", "分辨率", "码率", "线路", "地址" }, rows, 8, 10, 12, 4, 0);
        Out.Dim("\n  m3u8 的分片是相对路径，签名却在清单的 query 上 —— 直接交给播放器会 403，");
        Out.Dim("  桌面端用 LocalMediaProxy 重写成分片带签名的绝对地址；下载用 download 命令。");
        return 0;
    }

    // ───────────────────────────── emotes ─────────────────────────────

    public static async Task<int> EmotesAsync(CliArgs args)
    {
        int limit = Math.Clamp(args.GetInt("limit", 30), 1, 1000);
        string? keyword = args.Get("keyword", "k");

        await EmoteCatalog.Shared.EnsureLoadedAsync(CliContext.Token);

        var all = EmoteCatalog.Shared.Entries;
        IEnumerable<EmoteEntry> matched = all;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            matched = all.Where(e =>
                e.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || e.Package.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var list = matched.ToList();

        if (args.Has("json"))
        {
            JsonOut.Write(new { total = all.Count, matched = list.Count, items = list.Take(limit) });
            return all.Count == 0 ? 1 : 0;
        }

        Out.Section($"表情字典 · {all.Count} 个 · {all.Select(e => e.Package).Distinct().Count()} 个包");
        Out.Dim($"缓存：{EmoteCatalog.CachePath}");

        if (all.Count == 0)
        {
            Out.Warn("字典是空的（接口失败且没有缓存）");
            return 1;
        }

        var packages = all.GroupBy(e => e.Package)
            .Select(g => new[] { g.Key, g.Count().ToString() })
            .ToList();
        Out.Line();
        Out.Table(new[] { "包", "数量" }, packages, 16, 6);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            Out.Line();
            Out.Dim($"匹配「{keyword}」的 {list.Count} 个：");
            var rows = list.Take(limit).Select(e => new[]
            {
                e.Id.ToString(),
                e.Package,
                e.Name,
                e.Url,
            }).ToList();
            Out.Table(new[] { "id", "包", "名称", "图片" }, rows, 6, 10, 12, 80);
        }

        return 0;
    }
}
