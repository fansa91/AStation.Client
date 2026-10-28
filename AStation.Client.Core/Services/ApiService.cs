using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("AStation.Client.Tests")]

namespace AStation_Client.Services;

/// <summary>
/// AcFun API 统一请求入口。
/// 负责 HttpClient 生命周期、Cookie 容器、UA/Referer 头、基础 URL 拼接与 JSON 序列化。
/// WinUI3 桌面端不受浏览器 CORS 限制，可直接调用 AcFun 内部接口。
/// </summary>
public class ApiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookieContainer;
    private readonly JsonSerializerOptions _jsonOptions;

    // 基础域名（参考 ACFUN_API.md 1.1 节）
    private const string BaseHost = "https://www.acfun.cn";
    private const string LiveHost = "https://api.kuaishouzt.com";
    private const string IdHost = "https://id.app.acfun.cn";

    /// <summary>网页扫码登录专用域名。注意不是 www.acfun.cn，用错域名会 404。</summary>
    private const string ScanHost = "https://scan.acfun.cn";

    /// <summary>直播站域名（SPA，房间列表数据 SSR 内嵌在 window.__INITIAL_STATE__）</summary>
    public const string LiveSiteHost = "https://live.acfun.cn";

    /// <summary>直播互动网关（拉流地址、礼物、弹幕通道）</summary>
    public const string LiveStreamHost = LiveHost;

    /// <summary>直播站域名，供外部拼 URL 使用</summary>
    public const string ScanBaseUrl = ScanHost;

    public ApiService()
    {
        _cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseHost),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // 通用请求头（只读接口实测仅需 UA，带 Referer 更稳）
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri(BaseHost);
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// GET 请求并反序列化为指定类型
    /// </summary>
    public async Task<T?> GetAsync<T>(string path, Dictionary<string, string>? queryParams = null, CancellationToken ct = default)
    {
        var url = BuildUrl(BaseHost, path, queryParams);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<T>(request, ct);
    }

    /// <summary>
    /// POST form 请求并反序列化
    /// </summary>
    public async Task<T?> PostFormAsync<T>(string path, Dictionary<string, string> formData, CancellationToken ct = default)
    {
        var url = BuildUrl(BaseHost, path);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return await SendAsync<T>(request, ct);
    }

    /// <summary>
    /// GET 原始字符串（用于 HTML 页面抓取，如首页 BigPipe、搜索结果页）
    /// </summary>
    public async Task<string?> GetStringAsync(string path, Dictionary<string, string>? queryParams = null, CancellationToken ct = default)
    {
        var url = BuildUrl(BaseHost, path, queryParams);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// 获取直播拉流地址（独立域名 api.kuaishouzt.com）
    /// </summary>
    public async Task<T?> GetLiveAsync<T>(string path, Dictionary<string, string>? queryParams = null, CancellationToken ct = default)
    {
        var url = BuildUrl(LiveHost, path, queryParams);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<T>(request, ct);
    }

    /// <summary>
    /// GET 请求（独立域名 id.app.acfun.cn，如验证码接口）
    /// </summary>
    public async Task<T?> GetIdAsync<T>(string path, CancellationToken ct = default)
    {
        var url = BuildUrl(IdHost, path);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<T>(request, ct);
    }

    /// 账号密码登录（独立域名 id.app.acfun.cn）
    /// </summary>
    public async Task<T?> PostIdAsync<T>(string path, Dictionary<string, string> formData, CancellationToken ct = default)
    {
        var url = BuildUrl(IdHost, path);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return await SendAsync<T>(request, ct);
    }

    /// <summary>
    /// GET 请求（独立域名 scan.acfun.cn，扫码登录相关）
    /// </summary>
    public async Task<T?> GetScanAsync<T>(string path, Dictionary<string, string>? queryParams = null, CancellationToken ct = default)
    {
        var url = BuildUrl(ScanHost, path, queryParams);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<T>(request, ct);
    }

    /// <summary>
    /// 从任意指定域名抓取原始字符串（用于 SSR 页面内嵌数据，如直播站 __INITIAL_STATE__）
    /// </summary>
    public async Task<string?> GetStringFromHostAsync(string host, string path, Dictionary<string, string>? queryParams = null, CancellationToken ct = default)
    {
        var url = BuildUrl(host, path, queryParams);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// 从任意指定域名 POST 表单（用于直播拉流地址等第三方网关）
    /// </summary>
    public async Task<T?> PostFormToHostAsync<T>(
        string host,
        string path,
        Dictionary<string, string> formData,
        Dictionary<string, string>? queryParams = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl(host, path, queryParams);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.Referrer = new Uri(host);
        return await SendAsync<T>(request, ct);
    }

    /// <summary>
    /// 读取指定域名下某个 Cookie（直播接口需要 _did）
    /// </summary>
    public string? GetCookieValue(string host, string name)
    {
        try
        {
            var cookies = _cookieContainer.GetCookies(new Uri(host));
            var cookie = cookies[name];
            return string.IsNullOrWhiteSpace(cookie?.Value) ? null : cookie!.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取当前 Cookie 容器（用于调试或外部播放器携带 Cookie）
    /// </summary>
    public CookieContainer GetCookieContainer() => _cookieContainer;

    internal static string BuildUrl(string baseHost, string path, Dictionary<string, string>? queryParams = null)
    {
        var sb = new StringBuilder(baseHost);
        if (!path.StartsWith("/")) sb.Append('/');
        sb.Append(path);

        if (queryParams != null && queryParams.Count > 0)
        {
            sb.Append('?');
            // 对参数键排序，确保 URL 生成结果确定性，方便测试与缓存
            var sortedKeys = queryParams.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            bool first = true;
            foreach (var key in sortedKeys)
            {
                if (!first) sb.Append('&');
                var value = queryParams[key];
                sb.Append(Uri.EscapeDataString(key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(value));
                first = false;
            }
        }
        return sb.ToString();
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

/// <summary>
/// AcFun 接口通用响应信封
/// {"result":0, ...}
/// result=0 成功；21 参数格式错误；11 网络错误
/// </summary>
public class AcFunResponse<T>
{
    public int Result { get; set; }
    public T? Data { get; set; }
    public string? ErrorMsg { get; set; }
    public string? HostName { get; set; }
}
