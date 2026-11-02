using System.Text;
using AStation_Client.Services;

namespace AStation_Client.Cli.Commands;

/// <summary>
/// 只读内容类命令：搜索 / 排行 / 视频详情 / 评论 / 弹幕 / UBB 解析。
/// 每个命令都能加 <c>--json</c> 输出原始结构，方便直接接别的脚本。
/// </summary>
public static class ContentCommands
{
    // ───────────────────────────── search ─────────────────────────────

    public static async Task<int> SearchAsync(CliArgs args)
    {
        string keyword = args.Require(0, "关键词");
        var sort = Format.ParseSort(args.Get("sort", "s"));
        int limit = Math.Clamp(args.GetInt("limit", 20), 1, 30);

        var result = await ServiceLocator.Search.SearchVideosAsync(keyword, sort, CliContext.Token);

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                keyword,
                sort = (int)sort,
                total = result.Total,
                returned = result.Items.Count,
                items = result.Items,
            });
            return 0;
        }

        Out.Section($"搜索 · {keyword} · {Format.SortLabel(sort)}");
        if (result.Items.Count == 0)
        {
            Out.Warn("没有结果（关键词太冷门？接口偶尔返回空列表）");
            return 1;
        }

        Out.Dim($"命中 {result.Total} 条；接口只下发前 30 条，翻页无效");
        var rows = result.Items.Take(limit).Select((item, i) => new[]
        {
            (i + 1).ToString(),
            item.Title,
            item.Author,
            item.ViewCount,
            item.Duration,
            item.ResourceId,
        }).ToList();

        Out.Table(new[] { "#", "标题", "UP 主", "播放", "时长", "acId" }, rows, 4, 36, 14, 12, 7, 10);
        return 0;
    }

    // ───────────────────────────── rank ─────────────────────────────

    public static async Task<int> RankAsync(CliArgs args)
    {
        int channelId = args.GetInt("channel", 0);
        var period = Format.ParsePeriod(args.Get("period", "p"));
        int limit = Math.Clamp(args.GetInt("limit", 20), 1, 50);

        var entries = await ServiceLocator.Rank.GetRankListAsync(channelId, period, limit, CliContext.Token);

        if (args.Has("json"))
        {
            JsonOut.Write(new { channelId, period = RankService.ToPeriodParam(period), items = entries });
            return 0;
        }

        Out.Section($"排行榜 · {RankService.ToPeriodLabel(period)} · channelId={channelId}");
        if (entries.Count == 0)
        {
            Out.Warn("榜单为空（channelId 可能不是一级频道，或者该周期没有数据）");
            return 1;
        }

        var rows = entries.Take(limit).Select(e => new[]
        {
            e.RankText,
            e.Title,
            e.Author,
            e.ViewText,
            e.DurationText,
            e.ResourceId,
        }).ToList();

        Out.Table(new[] { "#", "标题", "UP 主", "播放", "时长", "acId" }, rows, 4, 36, 14, 12, 7, 10);
        return 0;
    }

    // ───────────────────────────── video ─────────────────────────────

    public static async Task<int> VideoAsync(CliArgs args)
    {
        string dougaId = args.Require(0, "视频 acId（dougaId）");

        var detail = await ServiceLocator.VideoDetail.GetVideoDetailAsync(dougaId, CliContext.Token);
        if (detail is null)
        {
            Out.Error("没有取到视频详情（接口可能调整，或 acId 不对）");
            return 1;
        }

        if (args.Has("json"))
        {
            JsonOut.Write(detail);
            return 0;
        }

        Out.Section(detail.Title);
        Out.KeyValues(
            ("acId", detail.DougaId),
            ("UP 主", $"{detail.AuthorName}（uid {detail.AuthorId}，{VideoDetailService.FormatCount(detail.AuthorFans)} 粉丝）"),
            ("频道", $"{detail.ChannelName}（cid {detail.ChannelId}）"),
            ("数据", $"{VideoDetailService.FormatCount(detail.ViewCount)} 播放 · " +
                     $"{VideoDetailService.FormatCount(detail.DanmakuCount)} 弹幕 · " +
                     $"{VideoDetailService.FormatCount(detail.CommentCount)} 评论"),
            ("互动", $"点赞 {VideoDetailService.FormatCount(detail.LikeCount)} · " +
                     $"收藏 {VideoDetailService.FormatCount(detail.StowCount)} · " +
                     $"香蕉 {VideoDetailService.FormatCount(detail.BananaCount)}"),
            ("当前 videoId", detail.CurrentVideoId));

        if (!string.IsNullOrWhiteSpace(detail.Description))
        {
            Out.Line();
            Out.Dim("简介：");
            Out.Info("  " + Out.Truncate(Format.Content(detail.Description, singleLine: true), 160));
        }

        Out.Line();
        Out.Dim($"分P（{detail.Parts.Count}）：");
        var rows = detail.Parts.Select((p, i) => new[]
        {
            (i + 1).ToString(),
            p.Title,
            p.DurationText,
            p.DanmakuCount > 0 ? p.DanmakuCount.ToString() : "-",
            p.VideoId,
        }).ToList();

        Out.Table(new[] { "#", "标题", "时长", "弹幕", "videoId" }, rows, 4, 44, 8, 7, 12);
        return 0;
    }

    // ───────────────────────────── comments ─────────────────────────────

    public static async Task<int> CommentsAsync(CliArgs args)
    {
        string sourceId = args.Require(0, "视频 dougaId");
        int page = Math.Max(1, args.GetInt("page", 1));
        int sourceType = args.GetInt("type", 3);
        int limit = Math.Clamp(args.GetInt("limit", 10), 1, 50);
        bool raw = args.Has("raw");

        await EmoteCatalog.Shared.EnsureLoadedAsync(CliContext.Token);
        var result = await ServiceLocator.Comment.GetCommentsAsync(sourceId, sourceType, page, CliContext.Token);

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                sourceId,
                sourceType,
                result.Page,
                result.TotalPage,
                result.TotalCount,
                error = result.ErrorMessage,
                items = result.Items,
            });
            return result.ErrorMessage is null ? 0 : 1;
        }

        if (result.ErrorMessage is not null)
        {
            Out.Error(result.ErrorMessage);
            return 1;
        }

        Out.Section($"评论 · sourceId={sourceId} · 第 {result.Page}/{Math.Max(result.Page, result.TotalPage)} 页");
        Out.Dim($"共 {result.TotalCount} 条，本页 {result.Items.Count} 条");

        int shown = 0;
        foreach (var comment in result.Items)
        {
            if (shown++ >= limit) break;

            var flags = new StringBuilder();
            if (comment.IsSticky) flags.Append("[置顶] ");
            if (comment.IsUp) flags.Append("[UP] ");

            Out.Line();
            Out.Info($"  #{comment.Floor} {flags}{comment.UserName}  · {comment.PostDate}" +
                     (comment.LikeCount > 0 ? $"  · 赞 {comment.LikeCount}" : string.Empty));
            Out.Line("     " + Format.Content(comment.Content, raw, singleLine: true));

            foreach (var reply in comment.Replies)
            {
                string to = reply.HasReplyTo ? $"{reply.ReplyToText}：" : string.Empty;
                Out.Dim($"     └ {reply.UserName} {to}{Format.Content(reply.Content, raw, singleLine: true)}");
            }

            if (comment.HasHiddenReplies)
            {
                Out.Dim($"     └ {comment.HiddenReplyText}");
            }
            else if (comment.SubCommentCount > 0 && comment.Replies.Count == 0)
            {
                Out.Dim($"     └ 另有 {comment.SubCommentCount} 条回复（接口未下发）");
            }
        }

        if (result.HasMore)
        {
            Out.Dim($"\n  还有下一页：--page {result.Page + 1}");
        }
        return 0;
    }

    // ───────────────────────────── danmaku ─────────────────────────────

    public static async Task<int> DanmakuAsync(CliArgs args)
    {
        string videoId = args.Require(0, "分P 的 videoId（不是 acId）");
        int limit = Math.Clamp(args.GetInt("limit", 20), 1, 500);
        bool raw = args.Has("raw");

        await EmoteCatalog.Shared.EnsureLoadedAsync(CliContext.Token);
        var list = await ServiceLocator.Danmaku.GetClientDanmakusAsync(videoId, 500, CliContext.Token);

        if (args.Has("json"))
        {
            JsonOut.Write(new { videoId, count = list.Count, items = list });
            return list.Count == 0 ? 1 : 0;
        }

        Out.Section($"弹幕 · videoId={videoId} · 共 {list.Count} 条");
        if (list.Count == 0)
        {
            Out.Warn("没有弹幕（videoId 用错是最常见的原因：弹幕挂在分P 的 videoId 上，不是 acId）");
            return 1;
        }

        var rows = list.Take(limit).Select(d => new[]
        {
            Out.Duration(d.TimeSeconds),
            d.Mode switch { DanmakuMode.Top => "顶部", DanmakuMode.Bottom => "底部", _ => "滚动" },
            Format.Content(d.Text, raw, singleLine: true),
            d.Color,
        }).ToList();

        Out.Table(new[] { "时间", "位置", "内容", "颜色" }, rows, 7, 4, 56, 8);
        if (list.Count > limit) Out.Dim($"\n  只显示前 {limit} 条，用 --limit 调整");
        return 0;
    }

    // ───────────────────────────── ubb ─────────────────────────────

    public static async Task<int> UbbAsync(CliArgs args)
    {
        string text = args.Positional.Count > 0 ? string.Join(' ', args.Positional) : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CliUsageException("缺少参数：要解析的 UBB 文本（记得加引号）");
        }

        await EmoteCatalog.Shared.EnsureLoadedAsync(CliContext.Token);
        var segments = UbbParser.Parse(text);

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                emotes = EmoteCatalog.Shared.Count,
                segments = segments.Select(s => new { kind = s.Kind.ToString(), s.Text, s.Url, s.Package, s.Name }),
            });
            return 0;
        }

        Out.Section("UBB 解析");
        Out.Dim($"表情字典：{EmoteCatalog.Shared.Count} 个");
        foreach (var segment in segments)
        {
            string detail = segment.Kind switch
            {
                UbbSegmentKind.Emote => $"{EmoteLabel(segment)}  {segment.Url}",
                UbbSegmentKind.Image => segment.Url ?? string.Empty,
                UbbSegmentKind.LineBreak => "（换行）",
                _ => segment.Text,
            };
            Out.Info($"  {segment.Kind,-10} {detail}");
        }

        Out.Line();
        Out.Dim("可读文本：");
        Out.Info("  " + Format.Content(text, singleLine: true));
        return 0;
    }

    private static string EmoteLabel(UbbSegment segment)
        => string.IsNullOrEmpty(segment.Name)
            ? "[表情]"
            : string.IsNullOrEmpty(segment.Package) ? $"[{segment.Name}]" : $"[{segment.Package}·{segment.Name}]";
}
