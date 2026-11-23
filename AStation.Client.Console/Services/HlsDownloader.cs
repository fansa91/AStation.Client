using System.Text;
using System.Text.RegularExpressions;

namespace AStation_Client.Cli.Services;

/// <summary>
/// 极简 HLS 下载器：把 m3u8 的分片并发抓下来（限流 + 重试 + 单请求超时 + 断点续传），
/// 按序拼成一个 .ts。
///
/// 必须处理对的几个点：
///   1. AcFun 的分片写的是相对路径，而签名参数（sign / t / us）挂在清单自己的 query 上，
///      按 RFC 解析相对路径会把 query 丢掉 → 分片 403。这里解析后手动把清单的 query 补回去。
///   2. 清单可能带 EXT-X-KEY（AES-128），要根据 EXT-X-IV 或分片序号解一段。
///   3. 分片先落到「输出文件名 + .parts」目录（解密后的完整分片），全部就绪后再按序拼接成最终文件；
///      中途失败 / Ctrl+C 时目录保留，下次运行接着下（续传），输出不会留下半截文件。
/// </summary>
public sealed class HlsDownloader
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    /// <summary>分片并发抓取数</summary>
    private const int Concurrency = 4;

    /// <summary>单个请求的总超时（连接 + 响应头 + 读完内容都在内）</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>每个分片的最大尝试次数（首试 + 重试，指数退避）</summary>
    private const int MaxAttempts = 3;

    private readonly HttpClient _http;

    public HlsDownloader(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.acfun.cn/");
    }

    public sealed record Result(int SegmentCount, long Bytes, string OutputPath);

    /// <summary>
    /// 下载并拼接。<paramref name="progress"/> 回调参数为 0~1。
    /// </summary>
    public async Task<Result> DownloadAsync(
        string m3u8Url, string outputPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string playlist = await FetchTextAsync(m3u8Url, ct);
        string mediaUrl = m3u8Url;

        // 主清单：选码率最高的那一路
        string? variant = SelectVariant(playlist, m3u8Url);
        if (variant is not null)
        {
            mediaUrl = variant;
            playlist = await FetchTextAsync(mediaUrl, ct);
        }

        var parsed = ParseMediaPlaylist(playlist, mediaUrl);
        if (parsed.Segments.Count == 0)
        {
            throw new InvalidOperationException("清单里没有分片（可能不是 m3u8，或者地址已过期）");
        }

        byte[]? key = null;
        if (parsed.KeyUri is not null)
        {
            key = await FetchBytesWithRetryAsync(parsed.KeyUri, ct);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // 分片先落到「输出文件名 + .parts」目录（解密后的完整分片，写 .tmp 再改名保证不会出现半截），
        // 已存在的分片直接复用 —— 断点续传就靠它。全部就绪后再按序拼成最终文件，
        // 所以中途失败 / Ctrl+C 时输出不会是残缺的，下次运行还能接着下。
        var partsDir = outputPath + ".parts";
        Directory.CreateDirectory(partsDir);

        int total = parsed.Segments.Count;
        var tasks = new List<Task>(total + 1);
        var completed = 0;

        var gate = new SemaphoreSlim(Concurrency, Concurrency);

        // fMP4 的初始化段（AcFun 的 TS 清单里没有，留着以防清单格式变化）
        if (parsed.InitSegment is not null)
        {
            var initPath = Path.Combine(partsDir, "init");
            if (!File.Exists(initPath))
            {
                tasks.Add(FetchSegmentToFileAsync(gate, parsed.InitSegment, initPath, key: null, playlistIv: null, segmentIndex: 0, ct));
            }
        }

        for (int i = 0; i < total; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var partPath = Path.Combine(partsDir, $"{index:D6}.ts");

                if (File.Exists(partPath))
                {
                    // 续传：上一轮已经拿到的分片直接复用
                    progress?.Report(Interlocked.Increment(ref completed) / (double)total);
                    return;
                }

                await FetchSegmentToFileAsync(gate, parsed.Segments[index], partPath, key, parsed.Iv, index, ct);

                progress?.Report(Interlocked.Increment(ref completed) / (double)total);
            }, ct));
        }

        await Task.WhenAll(tasks);

        long totalBytes = 0;
        await using (var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var initPath = Path.Combine(partsDir, "init");
            if (File.Exists(initPath))
            {
                totalBytes += await CopyFileToAsync(initPath, file, ct);
            }

            for (int i = 0; i < total; i++)
            {
                totalBytes += await CopyFileToAsync(Path.Combine(partsDir, $"{i:D6}.ts"), file, ct);
            }
        }

        Directory.Delete(partsDir, recursive: true);

        return new Result(total, totalBytes, outputPath);
    }

    /// <summary>限流抓一个分片：重试 + 指数退避 + 解密 + .tmp→改名原子落盘</summary>
    private async Task FetchSegmentToFileAsync(
        SemaphoreSlim gate,
        string url,
        string targetPath,
        byte[]? key,
        byte[]? playlistIv,
        int segmentIndex,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            await gate.WaitAsync(ct);
            try
            {
                var data = await FetchBytesAsync(url, ct);

                if (key is not null)
                {
                    var iv = playlistIv ?? SequenceIv(segmentIndex);
                    data = Decrypt(data, key, iv);
                }

                var tmpPath = targetPath + ".tmp";
                await File.WriteAllBytesAsync(tmpPath, data, ct);
                File.Move(tmpPath, targetPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt - 1)), ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>带重试与退避的字节抓取（密钥等小资源用）</summary>
    private async Task<byte[]> FetchBytesWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await FetchBytesAsync(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    private static async Task<long> CopyFileToAsync(string path, FileStream target, CancellationToken ct)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await source.CopyToAsync(target, ct);
        return source.Length;
    }

    // ───────────────────────────── 清单解析 ─────────────────────────────

    private sealed class MediaPlaylist
    {
        public List<string> Segments { get; } = new();
        public string? KeyUri { get; set; }
        public byte[]? Iv { get; set; }
        public string? InitSegment { get; set; }
    }

    private static MediaPlaylist ParseMediaPlaylist(string playlist, string baseUrl)
    {
        var result = new MediaPlaylist();

        foreach (var rawLine in playlist.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributes(line);
                if (attributes.TryGetValue("METHOD", out var method) && method.Equals("AES-128", StringComparison.OrdinalIgnoreCase)
                    && attributes.TryGetValue("URI", out var uri))
                {
                    result.KeyUri = Resolve(baseUrl, uri);
                    if (attributes.TryGetValue("IV", out var iv)) result.Iv = ParseIv(iv);
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributes(line);
                if (attributes.TryGetValue("URI", out var uri)) result.InitSegment = Resolve(baseUrl, uri);
                continue;
            }

            if (line[0] == '#') continue;

            result.Segments.Add(Resolve(baseUrl, line));
        }

        return result;
    }

    private static string? SelectVariant(string playlist, string baseUrl)
    {
        if (!playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) return null;

        int bestBandwidth = -1;
        string? best = null;

        var lines = playlist.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;

            int bandwidth = 0;
            var attributes = ParseAttributes(line);
            if (attributes.TryGetValue("BANDWIDTH", out var raw)) int.TryParse(raw, out bandwidth);

            string? uri = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                string candidate = lines[j].Trim();
                if (candidate.Length == 0 || candidate[0] == '#') continue;
                uri = candidate;
                break;
            }

            if (uri is null) continue;
            if (bandwidth <= bestBandwidth) continue;

            bestBandwidth = bandwidth;
            best = Resolve(baseUrl, uri);
        }

        return best;
    }

    private static readonly Regex AttributeRegex = new("(?<key>[A-Z0-9\\-]+)=(?<value>\"[^\"]*\"|[^,]*)", RegexOptions.IgnoreCase);

    private static Dictionary<string, string> ParseAttributes(string line)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(line))
        {
            dict[match.Groups["key"].Value] = match.Groups["value"].Value.Trim('"');
        }
        return dict;
    }

    /// <summary>
    /// 相对路径解析 + 把清单的 query 补回分片。
    /// 这一步是能不能下下来的关键：签在 query 上，丢了就是 403。
    /// </summary>
    internal static string Resolve(string baseUrl, string relative)
    {
        if (Uri.TryCreate(relative, UriKind.Absolute, out _)) return relative;

        var baseUri = new Uri(baseUrl);
        var resolved = new Uri(baseUri, relative);
        if (!string.IsNullOrEmpty(resolved.Query) || string.IsNullOrEmpty(baseUri.Query)) return resolved.ToString();

        var builder = new UriBuilder(resolved) { Query = baseUri.Query.TrimStart('?') };
        return builder.Uri.ToString();
    }

    private static byte[] ParseIv(string value)
    {
        string hex = value.Trim().TrimStart('0', 'x', 'X');
        if (hex.Length == 0) return new byte[16];

        hex = hex.PadLeft(32, '0');
        var iv = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            iv[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return iv;
    }

    /// <summary>没有显式 IV 时用分片序号（16 字节大端）作为 IV</summary>
    private static byte[] SequenceIv(int index)
    {
        var iv = new byte[16];
        iv[12] = (byte)(index >> 24);
        iv[13] = (byte)(index >> 16);
        iv[14] = (byte)(index >> 8);
        iv[15] = (byte)index;
        return iv;
    }

    private static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;

        try
        {
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 分片长度正好是 16 的倍数时可能没有填充，退一步按无填充解
            aes.Padding = System.Security.Cryptography.PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length);
        }
    }

    // ───────────────────────────── HTTP ─────────────────────────────

    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        // HttpClient.Timeout 是 Infinite：给每个请求挂一个总超时（连接 + 头 + 读完），
        // 否则 CDN 半死不活时单连接会永久挂起
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    private async Task<byte[]> FetchBytesAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    /// <summary>给日志用的短地址</summary>
    public static string Shorten(string url)
    {
        var builder = new StringBuilder(url);
        int query = url.IndexOf('?');
        if (query > 0 && builder.Length > 80) builder.Length = 80;
        return builder.ToString();
    }
}
