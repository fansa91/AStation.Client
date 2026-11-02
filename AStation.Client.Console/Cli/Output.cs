using System.Text;

namespace AStation_Client.Cli;

/// <summary>
/// 终端输出。表格按「显示宽度」对齐——中文标题一个字符占两格，
/// 直接用 string.Length 补空格会让每一列都歪。
/// </summary>
public static class Out
{
    private static bool ColorEnabled =>
        !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Section(string title)
    {
        Line();
        Write(title, ConsoleColor.Cyan);
        Line(new string('─', Math.Min(60, Math.Max(8, Width(title)))));
    }

    public static void Info(string text) => Line(text);

    public static void Ok(string text) => Write(text, ConsoleColor.Green);

    public static void Warn(string text) => Write(text, ConsoleColor.Yellow);

    public static void Error(string text) => Write(text, ConsoleColor.Red);

    public static void Dim(string text) => Write(text, ConsoleColor.DarkGray);

    /// <summary>诊断信息走 stderr：<c>--json</c> 的 stdout 要留给管道</summary>
    public static void Diag(string text) => Console.Error.WriteLine(text);

    public static void KeyValues(params (string Key, string? Value)[] pairs)
    {
        int keyWidth = pairs.Length == 0 ? 0 : pairs.Max(p => Width(p.Key));
        foreach (var (key, value) in pairs)
        {
            Line($"  {Pad(key, keyWidth)}  {value ?? "-"}");
        }
    }

    /// <summary>
    /// 打印表格。<paramref name="trim"/> 为每列最大显示宽度（超出用省略号）。
    /// </summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, params int[] trim)
    {
        if (rows.Count == 0)
        {
            Dim("（没有数据）");
            return;
        }

        int columns = headers.Count;
        var widths = new int[columns];

        for (int c = 0; c < columns; c++)
        {
            int limit = trim.Length > c && trim[c] > 0 ? trim[c] : int.MaxValue;
            widths[c] = Math.Min(limit, Math.Max(Width(headers[c]), rows.Max(r => c < r.Count ? Width(r[c]) : 0)));
        }

        var head = new StringBuilder("  ");
        for (int c = 0; c < columns; c++)
        {
            head.Append(Pad(headers[c], widths[c])).Append("  ");
        }
        Write(head.ToString().TrimEnd(), ConsoleColor.DarkGray);

        foreach (var row in rows)
        {
            var line = new StringBuilder("  ");
            for (int c = 0; c < columns; c++)
            {
                string cell = c < row.Count ? row[c] : string.Empty;
                line.Append(Pad(Truncate(cell, widths[c]), widths[c])).Append("  ");
            }
            Line(line.ToString().TrimEnd());
        }
    }

    // ───────────── 宽度（CJK 算 2 格） ─────────────

    public static int Width(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWide(rune) ? 2 : 1;
        }
        return width;
    }

    public static string Pad(string? text, int width)
    {
        text ??= string.Empty;
        int pad = width - Width(text);
        return pad <= 0 ? text : text + new string(' ', pad);
    }

    public static string Truncate(string? text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0 || Width(text) <= maxWidth) return text ?? string.Empty;

        var sb = new StringBuilder();
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int w = IsWide(rune) ? 2 : 1;
            if (width + w > maxWidth - 1) break;
            sb.Append(rune.ToString());
            width += w;
        }
        return sb.Append('…').ToString();
    }

    /// <summary>粗略判宽：中日韩文字、全角标点、常见 emoji 算两格</summary>
    private static bool IsWide(Rune rune)
    {
        int cp = rune.Value;
        return cp is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F9FF
            or >= 0x20000 and <= 0x3FFFD;
    }

    private static void Write(string text, ConsoleColor color)
    {
        if (!ColorEnabled)
        {
            Line(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    /// <summary>能不能用 \r 原地刷新进度（被重定向到管道 / 文件时不要刷屏）</summary>
    public static bool SupportsProgress => !Console.IsOutputRedirected;

    /// <summary>毫秒 → mm:ss / h:mm:ss</summary>
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "-";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }
}
