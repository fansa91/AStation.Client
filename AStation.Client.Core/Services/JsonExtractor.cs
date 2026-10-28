namespace AStation_Client.Services;

/// <summary>
/// 从 HTML/JS 源码里抠出内嵌 JSON。
/// AcFun 的页面（视频页、番剧页、直播站）都是把数据直接塞进 window.xxx = {...}，
/// 只要按大括号配平就能安全取出整块 JSON（正则做不到，会跨对象贪婪匹配）。
/// </summary>
internal static class JsonExtractor
{
    /// <summary>在源码里找 marker，并从其后的第一个 { 或 [ 开始取配平的 JSON 文本</summary>
    public static string? Extract(string? source, string marker)
    {
        if (string.IsNullOrEmpty(source)) return null;
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        return ExtractFrom(source, index);
    }

    /// <summary>从指定下标之后开始取配平的 JSON 文本</summary>
    public static string? ExtractFrom(string source, int startIndex)
    {
        int start = source.IndexOfAny(new[] { '{', '[' }, startIndex);
        if (start < 0) return null;

        char open = source[start];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < source.Length; i++)
        {
            char c = source[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return source.Substring(start, i - start + 1);
            }
        }

        return null;
    }

    /// <summary>取形如 "user":{...} 这种带键名的嵌套对象</summary>
    public static string? ExtractProperty(string? source, string propertyName)
    {
        if (string.IsNullOrEmpty(source)) return null;
        int index = source.IndexOf($"\"{propertyName}\":", StringComparison.Ordinal);
        if (index < 0) return null;
        return ExtractFrom(source, index);
    }
}
