using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AStation_Client.Services;

/// <summary>UBB 片段类型</summary>
public enum UbbSegmentKind
{
    Text,
    /// <summary>表情，<see cref="UbbSegment.Url"/> 为图片地址；解析不到时退化成文字「[表情]」</summary>
    Emote,
    /// <summary>图片评论，<see cref="UbbSegment.Url"/> 为图片地址</summary>
    Image,
    LineBreak
}

/// <summary>UBB 解析出来的一段内容</summary>
public sealed class UbbSegment
{
    public UbbSegmentKind Kind { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? Url { get; init; }

    /// <summary>表情所属包名（如「新娘」），仅在 Emote 上有值</summary>
    public string? Package { get; init; }

    /// <summary>表情名（如「笑」），仅在 Emote 上有值</summary>
    public string? Name { get; init; }
}

/// <summary>
/// 评论 / 弹幕正文的 UBB 解析。
///
/// AcFun 的正文是 UBB 而不是 HTML，实测评论区里会出现的标记（热门视频 65 条抽样）：
///   [emot=acfun,2789/]                表情，最常见（26/65）
///   [img=图片]https://imgs.aixifan.com/...[/img]   图片评论
///   [at uid=23682490]@AC娘本体[/at]     @ 某人
/// 另外网页端还保留 <c>[emot=pkg,NN/]</c> 这种老式 AC娘 图（umeditor 目录下的 gif）。
///
/// 解析成片段列表后由 UI 层决定怎么画：评论用 RichTextBlock（文字 + 内联表情图），
/// 弹幕用单行富文本。这样同一套规则，评论和弹幕不会各写一份正则。
/// </summary>
public static class UbbParser
{
    /// <summary>表情码：[emot=包名,编号/]</summary>
    private static readonly Regex EmoteRegex = new(
        @"\[emot=(?<pkg>[A-Za-z0-9_\-]+),(?<id>[^/\]]+)/\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>@ 某人：[at uid=123] 名字 [/at]</summary>
    private static readonly Regex AtRegex = new(
        @"\[at\s+uid=(?<uid>\d+)\s*\](?<name>.*?)\[/at\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>图片：[img]url[/img] / [img=图片]url[/img]</summary>
    private static readonly Regex ImageRegex = new(
        @"\[img(?:=[^\]]*)?\](?<url>[^\[\]]+)\[/img\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>换行：[br] / [br/]</summary>
    private static readonly Regex BreakRegex = new(
        @"\[br\s*/?\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>其余样式标签：直接去掉，只保留里面的文字</summary>
    private static readonly Regex StyleTagRegex = new(
        @"\[/?(?:b|i|u|s|sup|sub|color|font|size|url|align|center|left|right|quote|code|table|tr|td|h[1-6])(?:=[^\]]*)?\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string EmotePlaceholder = "[表情]";

    /// <summary>正文里是否有需要解析的标记（弹幕走这个判断决定是否需要富文本）</summary>
    public static bool HasMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        return text.Contains("[emot=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[img", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[at", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[br", StringComparison.OrdinalIgnoreCase)
            || text.Contains('\n');
    }

    /// <summary>
    /// 解析正文。表情地址默认查 <see cref="EmoteCatalog.Shared"/>，
    /// 单测 / 离线场景可以传入自己的解析器。
    /// </summary>
    public static List<UbbSegment> Parse(string? content, Func<long, string?>? emoteLookup = null)
    {
        var segments = new List<UbbSegment>();
        if (string.IsNullOrEmpty(content)) return segments;

        emoteLookup ??= id => EmoteCatalog.Shared.GetUrl(id);

        int cursor = 0;
        while (cursor < content.Length)
        {
            var match = FindNext(content, cursor);
            if (match is null)
            {
                AppendText(segments, content[cursor..]);
                break;
            }

            if (match.Index > cursor) AppendText(segments, content[cursor..match.Index]);

            switch (match.Type)
            {
                case TokenType.Emote:
                    AppendEmote(segments, match.Package!, match.Id!, emoteLookup);
                    break;

                case TokenType.At:
                    segments.Add(new UbbSegment
                    {
                        Kind = UbbSegmentKind.Text,
                        Text = "@" + match.Name!.Trim().TrimStart('@').Trim()
                    });
                    break;

                case TokenType.Image:
                    var imageUrl = match.Url?.Trim();
                    if (IsWebUrl(imageUrl))
                    {
                        segments.Add(new UbbSegment { Kind = UbbSegmentKind.Image, Url = imageUrl });
                    }

                    break;

                case TokenType.Break:
                    segments.Add(new UbbSegment { Kind = UbbSegmentKind.LineBreak });
                    break;
            }

            cursor = match.Index + match.Length;
        }

        return Coalesce(segments);
    }

    /// <summary>
    /// 合并相邻的文字片段：@ 和换行会把一段文字切成几块，
    /// 不合并的话一条评论会多出十几个 Run，测量和排版都更贵。
    /// </summary>
    private static List<UbbSegment> Coalesce(List<UbbSegment> segments)
    {
        if (segments.Count < 2) return segments;

        var merged = new List<UbbSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.Kind == UbbSegmentKind.Text
                && merged.Count > 0
                && merged[^1].Kind == UbbSegmentKind.Text)
            {
                merged[^1] = new UbbSegment
                {
                    Kind = UbbSegmentKind.Text,
                    Text = merged[^1].Text + segment.Text
                };
                continue;
            }

            merged.Add(segment);
        }

        return merged;
    }

    /// <summary>纯文字版本（日志 / 摘要 / 无障碍名称用）</summary>
    public static string ToPlainText(string? content)
    {
        var segments = Parse(content);
        if (segments.Count == 0) return string.Empty;

        var builder = new StringBuilder(content.Length);
        foreach (var segment in segments)
        {
            builder.Append(segment.Kind switch
            {
                UbbSegmentKind.Emote => EmotePlaceholder,
                UbbSegmentKind.Image => "[图片]",
                UbbSegmentKind.LineBreak => "\n",
                _ => segment.Text
            });
        }

        return builder.ToString();
    }

    // ───────────────────────────── 内部实现 ─────────────────────────────

    private static void AppendEmote(
        List<UbbSegment> segments, string package, string code, Func<long, string?> emoteLookup)
    {
        string url = ResolveEmoteUrl(package, code, emoteLookup, out string? packageName, out string? name);

        if (string.IsNullOrWhiteSpace(url))
        {
            segments.Add(new UbbSegment { Kind = UbbSegmentKind.Text, Text = EmotePlaceholder });
            return;
        }

        segments.Add(new UbbSegment
        {
            Kind = UbbSegmentKind.Emote,
            Url = url,
            Package = packageName,
            Name = name
        });
    }

    /// <summary>
    /// 表情地址规则（对齐网页端 pc-comment 的实现）：
    ///   acfun,1738        → 字典里的真实地址
    ///   ac,13             → 老 UMeditor 目录下的 gif
    ///   acfun,不认识      → 文字占位，避免挂一张 404 图
    /// </summary>
    internal static string ResolveEmoteUrl(
        string package, string code, Func<long, string?> emoteLookup,
        out string? packageName, out string? name)
    {
        packageName = null;
        name = null;

        if (package.Equals("acfun", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(code, out long id) || id <= 0) return string.Empty;

            string? url = emoteLookup(id);
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            if (EmoteCatalog.Shared.TryGet(id, out var entry))
            {
                packageName = entry.Package;
                name = entry.Name;
            }

            return url!;
        }

        // 老式 [emot=ac,13/]
        if (package.Equals("ac", StringComparison.OrdinalIgnoreCase))
        {
            string number = code.Trim();
            if (!int.TryParse(number, out int n) || n <= 0) return string.Empty;

            packageName = "AC娘";
            name = number;
            return EmoteCatalog.LegacyAcEmoteBase + n.ToString("00") + ".gif";
        }

        return string.Empty;
    }

    private static void AppendText(List<UbbSegment> segments, string raw)
    {
        if (raw.Length == 0) return;

        string text = StyleTagRegex.Replace(raw, string.Empty);
        text = WebUtility.HtmlDecode(text);
        if (text.Length == 0) return;

        // TextBlock 里 \r 会占位，统一处理成换行
        var pieces = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < pieces.Length; i++)
        {
            if (i > 0) segments.Add(new UbbSegment { Kind = UbbSegmentKind.LineBreak });
            if (pieces[i].Length > 0)
            {
                segments.Add(new UbbSegment { Kind = UbbSegmentKind.Text, Text = pieces[i] });
            }
        }
    }

    private static bool IsWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>在 cursor 之后找到最早出现的标记（三种标记可能交错）</summary>
    private static Token? FindNext(string content, int cursor)
    {
        Token? best = null;

        void Consider(Match? match, TokenType type)
        {
            if (match is null || !match.Success) return;
            if (best is not null && match.Index >= best.Index) return;

            best = new Token
            {
                Type = type,
                Index = match.Index,
                Length = match.Length,
                Package = match.Groups["pkg"].Success ? match.Groups["pkg"].Value : null,
                Id = match.Groups["id"].Success ? match.Groups["id"].Value : null,
                Name = match.Groups["name"].Success ? match.Groups["name"].Value : null,
                Url = match.Groups["url"].Success ? match.Groups["url"].Value : null
            };
        }

        Consider(EmoteRegex.Match(content, cursor), TokenType.Emote);
        Consider(AtRegex.Match(content, cursor), TokenType.At);
        Consider(ImageRegex.Match(content, cursor), TokenType.Image);
        Consider(BreakRegex.Match(content, cursor), TokenType.Break);

        return best;
    }

    private enum TokenType
    {
        Emote,
        At,
        Image,
        Break
    }

    private sealed class Token
    {
        public TokenType Type { get; init; }

        public int Index { get; init; }

        public int Length { get; init; }

        public string? Package { get; init; }

        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Url { get; init; }
    }
}
