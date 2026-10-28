using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AStation_Client.Services;

/// <summary>
/// 本地媒体代理（只监听 127.0.0.1，随机端口）。
///
/// 存在的原因：
/// AcFun 的 HLS 清单里分片写的是**相对路径**，而 CDN 的签名参数（sign / t / us）
/// 是挂在 m3u8 自己的 query 上的。播放器按 RFC 3986 解析相对路径时会丢掉 query，
/// 于是所有分片请求都变成 403 —— 系统解码器报「视频类型不受支持或文件路径无效」，
/// FFmpeg 也一样解不出来（它同样遵守规范）。
///
/// 这里起一个极小的 HTTP 服务，把远端清单抓下来、把每个分片重写成「带签名的绝对地址」，
/// 再用播放器能直接访问的本地地址暴露出去：
///   GET /hls/{token}.m3u8     远端清单（重写后）
///   GET /seg/{token}/{index}  第 index 个分片（透传 Range）
///   GET /raw/{token}          任意资源透传（EXT-X-KEY / EXT-X-MAP 用）
/// </summary>
public sealed class LocalMediaProxy : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    private const string Referer = "https://www.acfun.cn/";

    private static readonly Lazy<LocalMediaProxy> LazyInstance = new(() => new LocalMediaProxy());

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 分片本身不压缩，别让 HttpClient 擅自解码后 Content-Length 对不上
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 32
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static LocalMediaProxy Current => LazyInstance.Value;

    private readonly ConcurrentDictionary<string, HlsSession> _playlists = new();
    private readonly ConcurrentDictionary<string, string> _rawResources = new();
    private readonly object _gate = new();

    private TcpListener? _listener;
    private int _port;

    private sealed class HlsSession
    {
        public string SourceUrl = string.Empty;
        public string? PlaylistText;
        public List<string> Segments = new();
    }

    private LocalMediaProxy() { }

    /// <summary>把远端 HLS 清单注册进代理，返回播放器可用的本地地址。</summary>
    public string RegisterHls(string m3u8Url)
    {
        EnsureStarted();

        var token = Guid.NewGuid().ToString("N");
        _playlists[token] = new HlsSession { SourceUrl = m3u8Url };

        return $"http://127.0.0.1:{_port}/hls/{token}.m3u8";
    }

    /// <summary>把任意地址挂到代理后面透传。</summary>
    public string RegisterRaw(string url)
    {
        EnsureStarted();

        var token = Guid.NewGuid().ToString("N");
        _rawResources[token] = url;

        return $"http://127.0.0.1:{_port}/raw/{token}";
    }

    // ───────────────────────────── 服务端 ─────────────────────────────

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_listener is not null) return;

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            _listener = listener;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;

            _ = AcceptLoopAsync(listener);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener 被停掉
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                using var stream = client.GetStream();

                // HTTP/1.1 保持连接：一个 TCP 连接上跑多个请求，分片加载才不卡
                while (true)
                {
                    var head = await ReadHeadAsync(stream).ConfigureAwait(false);
                    if (head is null) return;

                    await DispatchAsync(stream, head).ConfigureAwait(false);

                    if (string.Equals(GetHeader(head, "Connection"), "close", StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }
            catch
            {
                // 播放器切清晰度/关页面时会突然断开，属于正常现象
            }
        }
    }

    private async Task DispatchAsync(NetworkStream stream, string head)
    {
        var requestLine = head.Split("\r\n")[0];
        var parts = requestLine.Split(' ');

        if (parts.Length < 2)
        {
            await WriteStatusAsync(stream, 400, "bad request").ConfigureAwait(false);
            return;
        }

        var method = parts[0];
        var target = parts[1];

        int queryStart = target.IndexOf('?');
        var path = queryStart >= 0 ? target[..queryStart] : target;

        if (path.StartsWith("/hls/", StringComparison.Ordinal))
        {
            var token = path["/hls/".Length..];
            if (token.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) token = token[..^5];

            await ServePlaylistAsync(stream, token).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/seg/", StringComparison.Ordinal))
        {
            var rest = path["/seg/".Length..];
            int slash = rest.IndexOf('/');

            if (slash > 0
                && _playlists.TryGetValue(rest[..slash], out var session)
                && int.TryParse(rest[(slash + 1)..], out int index)
                && index >= 0 && index < session.Segments.Count)
            {
                await ForwardAsync(stream, session.Segments[index], head, method).ConfigureAwait(false);
                return;
            }

            await WriteStatusAsync(stream, 404, "segment not found").ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/raw/", StringComparison.Ordinal))
        {
            var token = path["/raw/".Length..];

            if (_rawResources.TryGetValue(token, out var url))
            {
                await ForwardAsync(stream, url, head, method).ConfigureAwait(false);
                return;
            }

            await WriteStatusAsync(stream, 404, "resource not found").ConfigureAwait(false);
            return;
        }

        await WriteStatusAsync(stream, 404, "not found").ConfigureAwait(false);
    }

    // ───────────────────────────── 清单重写 ─────────────────────────────

    private async Task ServePlaylistAsync(NetworkStream stream, string token)
    {
        if (!_playlists.TryGetValue(token, out var session))
        {
            await WriteStatusAsync(stream, 404, "unknown playlist").ConfigureAwait(false);
            return;
        }

        if (session.PlaylistText is null)
        {
            using var request = CreateRequest(session.SourceUrl, Get);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await WriteStatusAsync(stream, (int)response.StatusCode, "upstream playlist unavailable").ConfigureAwait(false);
                return;
            }

            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            lock (session)
            {
                session.PlaylistText ??= RewritePlaylist(raw, token, session);
            }
        }

        var payload = Encoding.UTF8.GetBytes(session.PlaylistText);

        var head = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: application/vnd.apple.mpegurl\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Cache-Control: no-cache\r\n")
            .Append("Connection: keep-alive\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
    }

    /// <summary>
    /// 把清单里的相对分片改写成「绝对地址 + 原清单的签名 query」，
    /// 并换成指向本代理的完整 URL。
    /// </summary>
    private string RewritePlaylist(string raw, string token, HlsSession session)
    {
        var baseUri = new Uri(session.SourceUrl);
        var output = new StringBuilder(raw.Length * 2);

        foreach (var line in raw.Split('\n'))
        {
            var text = line.Trim('\r', ' ', '\t');
            if (text.Length == 0) continue;

            if (text[0] == '#')
            {
                // EXT-X-KEY / EXT-X-MAP 里的 URI 也要过代理，否则同样 403
                if (text.StartsWith("#EXT-X-KEY", StringComparison.Ordinal)
                    || text.StartsWith("#EXT-X-MAP", StringComparison.Ordinal)
                    || text.StartsWith("#EXT-X-MEDIA", StringComparison.Ordinal))
                {
                    text = RewriteTagUri(text, baseUri);
                }

                output.Append(text).Append('\n');
                continue;
            }

            var absolute = ResolveWithQuery(baseUri, text);
            session.Segments.Add(absolute);

            output.Append("http://127.0.0.1:").Append(_port)
                  .Append("/seg/").Append(token).Append('/')
                  .Append(session.Segments.Count - 1)
                  .Append('\n');
        }

        return output.ToString();
    }

    private string RewriteTagUri(string tag, Uri baseUri)
    {
        const string marker = "URI=\"";

        int start = tag.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return tag;

        int valueStart = start + marker.Length;
        int valueEnd = tag.IndexOf('"', valueStart);
        if (valueEnd < 0) return tag;

        var uri = tag[valueStart..valueEnd];
        if (uri.Length == 0) return tag;

        var token = Guid.NewGuid().ToString("N");
        _rawResources[token] = ResolveWithQuery(baseUri, uri);

        return string.Concat(
            tag[..valueStart],
            "http://127.0.0.1:", _port.ToString(), "/raw/", token,
            tag[valueEnd..]);
    }

    /// <summary>
    /// 关键的一步：按规范解析相对地址会把 query 丢掉，而签名就在 query 上，必须补回去。
    /// </summary>
    private static string ResolveWithQuery(Uri baseUri, string reference)
    {
        var resolved = new Uri(baseUri, reference);

        // 分片自己带了 query，或者清单本来就没有 query —— 保持原样
        if (resolved.Query.Length > 0 || baseUri.Query.Length == 0) return resolved.AbsoluteUri;

        var builder = new UriBuilder(resolved) { Query = baseUri.Query.TrimStart('?') };
        return builder.Uri.AbsoluteUri;
    }

    // ───────────────────────────── 转发 ─────────────────────────────

    private async Task ForwardAsync(NetworkStream stream, string url, string head, string method)
    {
        var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

        using var request = CreateRequest(url, isHead ? HttpMethod.Head : HttpMethod.Get);

        var range = GetHeader(head, "Range");
        if (!string.IsNullOrEmpty(range))
            request.Headers.TryAddWithoutValidation("Range", range);

        HttpResponseMessage response;

        try
        {
            response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteStatusAsync(stream, 502, ex.Message).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            var contentLength = response.Content.Headers.ContentLength;

            var headerBuilder = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");

            var contentType = response.Content.Headers.ContentType?.ToString();
            if (!string.IsNullOrEmpty(contentType))
                headerBuilder.Append("Content-Type: ").Append(contentType).Append("\r\n");

            var contentRange = response.Content.Headers.ContentRange?.ToString();
            if (!string.IsNullOrEmpty(contentRange))
                headerBuilder.Append("Content-Range: ").Append(contentRange).Append("\r\n");

            headerBuilder.Append("Accept-Ranges: bytes\r\n");

            if (contentLength.HasValue)
                headerBuilder.Append("Content-Length: ").Append(contentLength.Value).Append("\r\n");
            else
                headerBuilder.Append("Transfer-Encoding: chunked\r\n");

            headerBuilder.Append("Connection: keep-alive\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headerBuilder.ToString())).ConfigureAwait(false);

            if (isHead) return;

            using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            if (contentLength.HasValue)
            {
                await body.CopyToAsync(stream, 81920).ConfigureAwait(false);
                return;
            }

            var buffer = new byte[81920];
            int read;

            while ((read = await body.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{read:X}\r\n")).ConfigureAwait(false);
                await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                await stream.WriteAsync(Crlf).ConfigureAwait(false);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n")).ConfigureAwait(false);
        }
    }

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    private static HttpRequestMessage CreateRequest(string url, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", Referer);
        return request;
    }

    // ───────────────────────────── HTTP 小工具 ─────────────────────────────

    /// <summary>读到请求头结束（\r\n\r\n）为止</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();

        while (true)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            if (read <= 0) return null;

            accumulated.Write(buffer, 0, read);

            var text = Encoding.ASCII.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (end >= 0) return text[..end];
            if (accumulated.Length > 64 * 1024) return null;
        }
    }

    private static string? GetHeader(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private static async Task WriteStatusAsync(NetworkStream stream, int status, string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);

        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Connection: keep-alive\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        400 => "Bad Request",
        404 => "Not Found",
        403 => "Forbidden",
        502 => "Bad Gateway",
        _ => "Unknown"
    };

    public void Dispose()
    {
        lock (_gate)
        {
            try { _listener?.Stop(); } catch { /* 退出时无所谓 */ }
            _listener = null;
        }

        _playlists.Clear();
        _rawResources.Clear();
    }
}
