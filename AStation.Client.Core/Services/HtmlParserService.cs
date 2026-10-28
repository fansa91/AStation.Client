using System.Text.RegularExpressions;

namespace AStation_Client.Services;

/// <summary>
/// HTML 解析服务。
/// AcFun 首页（BigPipe 流式 HTML）和搜索结果页没有 JSON 接口，
/// 条目数据藏在 HTML 片段的 data-exposure-log 属性中。
/// </summary>
public class HtmlParserService
{
    /// <summary>
    /// 从搜索结果页 HTML 中提取视频条目。
    /// 条目链接格式：/v/ac{dougaId}
    /// 数据在 data-exposure-log 属性：{"content_id":..., "authorId":..., "title":...}
    /// </summary>
    public List<SearchResultItem> ParseSearchResults(string html)
    {
        var results = new List<SearchResultItem>();

        // 提取 data-exposure-log 属性中的 JSON
        var exposureRegex = new Regex(@"data-exposure-log=""([^""]+)""", RegexOptions.IgnoreCase);
        var matches = exposureRegex.Matches(html);

        foreach (Match match in matches)
        {
            try
            {
                var json = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var item = new SearchResultItem
                {
                    ContentId = root.TryGetProperty("content_id", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                    AuthorId = root.TryGetProperty("authorId", out var aid) ? aid.GetString() ?? string.Empty : string.Empty,
                    Title = root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    ServerRId = root.TryGetProperty("serverRId", out var srid) ? srid.GetString() ?? string.Empty : string.Empty,
                    ServerGId = root.TryGetProperty("serverGId", out var sgid) ? sgid.GetString() ?? string.Empty : string.Empty
                };

                // 从周围 HTML 提取 dougaId（链接 /v/ac{id}）
                var itemHtml = match.Value;
                var idMatch = Regex.Match(itemHtml, @"/v/ac(\d+)");
                if (idMatch.Success)
                {
                    item.DougaId = idMatch.Groups[1].Value;
                }

                results.Add(item);
            }
            catch
            {
                // 单条解析失败不影响整体
                continue;
            }
        }

        return results;
    }

    /// <summary>
    /// 从首页 BigPipe HTML 中提取 pagelet 数据（scripts 内嵌的 window.xxx JSON）。
    /// </summary>
    public List<string> ExtractPageletScripts(string html)
    {
        var scripts = new List<string>();
        var regex = new Regex(@"scripts\s*:\s*\[([^\]]+)\]", RegexOptions.IgnoreCase);
        var match = regex.Match(html);
        if (match.Success)
        {
            var content = match.Groups[1].Value;
            // 简单提取字符串
            var strMatches = Regex.Matches(content, "\"([^\"]+)\"");
            foreach (Match sm in strMatches)
            {
                scripts.Add(sm.Groups[1].Value);
            }
        }
        return scripts;
    }
}

/// <summary>
/// 搜索结果条目模型
/// </summary>
public class SearchResultItem
{
    public string ContentId { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ServerRId { get; set; } = string.Empty;
    public string ServerGId { get; set; } = string.Empty;
    public string DougaId { get; set; } = string.Empty; // 从链接 /v/ac{id} 提取
}
