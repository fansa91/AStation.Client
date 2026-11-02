using System.Text;
using AStation_Client.Models;
using AStation_Client.Services;

namespace AStation_Client.Cli;

/// <summary>
/// 把服务层的数据摆成终端里好读的样子。
/// 评论 / 弹幕正文是 UBB，这里统一转成人眼可读的文本（表情显示成「新娘·笑」）。
/// </summary>
public static class Format
{
    /// <summary>UBB 正文 → 单行可读文本</summary>
    public static string Content(string? ubb, bool raw = false, bool singleLine = false)
    {
        if (raw) return ubb ?? string.Empty;
        if (string.IsNullOrEmpty(ubb)) return string.Empty;
        if (!UbbParser.HasMarkup(ubb)) return ubb.Replace("\r", string.Empty).Replace('\n', ' ').Trim();

        var result = new StringBuilder();
        foreach (var segment in UbbParser.Parse(ubb))
        {
            switch (segment.Kind)
            {
                case UbbSegmentKind.Emote:
                    result.Append(EmoteLabel(segment));
                    break;

                case UbbSegmentKind.Image:
                    result.Append("[图片]");
                    break;

                case UbbSegmentKind.LineBreak:
                    result.Append(singleLine ? " / " : "\n");
                    break;

                default:
                    result.Append(segment.Text);
                    break;
            }
        }

        return result.ToString().Trim();
    }

    private static string EmoteLabel(UbbSegment segment)
    {
        if (string.IsNullOrEmpty(segment.Name)) return "[表情]";
        return string.IsNullOrEmpty(segment.Package)
            ? $"[{segment.Name}]"
            : $"[{segment.Package}·{segment.Name}]";
    }

    /// <summary>排序参数：1/2/5 或 relevance / view / new</summary>
    public static SearchSort ParseSort(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "1" or "relevance" or "综合" => SearchSort.Relevance,
        "2" or "view" or "views" or "most-viewed" or "播放" => SearchSort.MostViewed,
        "5" or "new" or "newest" or "最新" => SearchSort.Newest,
        _ => throw new CliUsageException($"不认识的排序「{value}」，可用：1 综合 / 2 最多播放 / 5 最新发布"),
    };

    public static string SortLabel(SearchSort sort) => sort switch
    {
        SearchSort.MostViewed => "最多播放",
        SearchSort.Newest => "最新发布",
        _ => "综合",
    };

    /// <summary>榜单周期：today / 3d / week</summary>
    public static RankPeriod ParsePeriod(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "day" or "today" or "日" => RankPeriod.Today,
        "3d" or "three" or "three-days" or "三日" => RankPeriod.ThreeDays,
        "week" or "7d" or "周" => RankPeriod.Week,
        _ => throw new CliUsageException($"不认识的周期「{value}」，可用：today / 3d / week"),
    };

    /// <summary>把「1080P」「720」「best」这类写法匹配到实际档位</summary>
    public static PlayQuality? PickQuality(IReadOnlyList<PlayQuality> qualities, string? wanted)
    {
        if (qualities.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(wanted) || wanted.Equals("best", StringComparison.OrdinalIgnoreCase))
        {
            return qualities[0];
        }

        string key = wanted.Trim().ToUpperInvariant().Replace("P", string.Empty).Replace("＋", "+");

        return qualities.FirstOrDefault(q => q.Label.ToUpperInvariant().Replace("P", string.Empty).Replace("＋", "+") == key)
            ?? qualities.FirstOrDefault(q => q.Height.ToString() == key)
            ?? throw new CliUsageException(
                $"没有「{wanted}」这一档，可用：{string.Join(" / ", qualities.Select(q => q.Label))}");
    }
}
