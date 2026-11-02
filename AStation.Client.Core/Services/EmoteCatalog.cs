using System.Text.Json;
using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 表情字典：把评论 / 弹幕里的 <c>[emot=acfun,1738/]</c> 映射成图片地址。
///
/// 数据源就是网页端评论组件用的同一个接口：
///   POST /rest/pc-direct/emotion/getUserEmotion  →  emotionPackageList[].emotions[]
///   每个 emotion 的 id 就是表情码里的数字（如 1738 = 新娘包·笑），
///   图片在 bigImageInfo.thumbnailImage.cdnUrls[0].url。
/// 接口不需要登录，一次返回全部 23 个包 / 871 个表情（约 1.4 MB JSON），
/// 所以结果落盘缓存，7 天内直接用缓存，过期先读旧缓存再后台刷新。
///
/// 拿不到（断网 / 接口变脸 / 首次冷启动）时退回网页端同款兜底：
/// AC娘 classic 55 张 <c>.../umeditor/dialogs/emotion/images/ac/NN.gif</c>。
/// </summary>
public sealed class EmoteCatalog
{
    /// <summary>缓存有效期：7 天</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    /// <summary>网页端用的经典 AC娘 表情（编号 01~55）</summary>
    internal const string LegacyAcEmoteBase =
        "https://cdn.aixifan.com/dotnet/20130418/umeditor/dialogs/emotion/images/ac/";

    private static readonly Lazy<EmoteCatalog> LazyShared = new(() => new EmoteCatalog());

    private readonly Dictionary<long, EmoteEntry> _byId = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _loaded;
    private Task? _loading;

    public static EmoteCatalog Shared => LazyShared.Value;

    /// <summary>字典已就绪（有内容）</summary>
    public bool IsLoaded => _loaded;

    public int Count => _byId.Count;

    /// <summary>全部表情（按 id 升序），供命令行工具 / 调试查看</summary>
    public IReadOnlyList<EmoteEntry> Entries => _byId.Values.OrderBy(e => e.Id).ToList();

    /// <summary>字典首次加载完成后触发，供控件重渲染</summary>
    public event EventHandler? Loaded;

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStation.Client",
        "emotes.json");

    /// <summary>按表情 id 查地址</summary>
    public bool TryGet(long id, out EmoteEntry entry) => _byId.TryGetValue(id, out entry);

    public string? GetUrl(long id) => _byId.TryGetValue(id, out var entry) ? entry.Url : null;

    /// <summary>
    /// 保证字典可用。并发调用只会真正加载一次；失败不抛异常（退回内置兜底）。
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return Task.CompletedTask;

        lock (_byId)
        {
            return _loading ??= LoadCoreAsync(ct);
        }
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        // 1) 先吃缓存（过期也用，先让界面有表情，再后台更新）
        CachePayload? cache = null;
        try
        {
            cache = ReadCache();
        }
        catch
        {
            // 缓存坏了就当没有
        }

        if (cache is { Entries.Count: > 0 })
        {
            Apply(cache.Entries);
            MarkLoaded();

            if (DateTimeOffset.UtcNow - cache.SavedAt < CacheTtl) return;
        }

        // 2) 拉接口；失败时保留缓存 / 兜底
        try
        {
            var list = await FetchAsync(ct).ConfigureAwait(false);
            if (list.Count > 0)
            {
                Apply(list);
                MarkLoaded();
                WriteCache(list);
                return;
            }
        }
        catch
        {
            // 网络问题走兜底
        }

        if (!_loaded) Apply(BuildLegacyFallback());
        MarkLoaded();
    }

    private void MarkLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            Loaded?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 订阅方异常不该影响字典
        }
    }

    private void Apply(IEnumerable<EmoteEntry> entries)
    {
        lock (_byId)
        {
            foreach (var entry in entries)
            {
                if (entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.Url)) continue;
                _byId[entry.Id] = entry;
            }
        }
    }

    // ───────────────────────────── 接口 ─────────────────────────────

    private static async Task<List<EmoteEntry>> FetchAsync(CancellationToken ct)
    {
        // 这个接口是 POST，但不需要任何参数（登录态只影响是否带自定义表情包）
        var response = await ServiceLocator.Api
            .PostFormAsync<JsonNode>("/rest/pc-direct/emotion/getUserEmotion", new Dictionary<string, string>(), ct)
            .ConfigureAwait(false);

        return ParsePackages(response);
    }

    /// <summary>解析 emotionPackageList（容错：字段缺失一律跳过，不让一个坏包毁掉整张字典）</summary>
    internal static List<EmoteEntry> ParsePackages(JsonNode? node)
    {
        var list = new List<EmoteEntry>();
        if (node?["emotionPackageList"] is not JsonArray packages) return list;

        foreach (var packageNode in packages)
        {
            if (packageNode is not JsonObject package) continue;

            string packageName = ReadString(package["name"]) ?? string.Empty;
            if (package["emotions"] is not JsonArray emotions) continue;

            foreach (var emotionNode in emotions)
            {
                if (emotionNode is not JsonObject emotion) continue;

                long id = ReadLong(emotion["id"]);
                if (id <= 0) continue;

                string? url = FirstCdnUrl(emotion["bigImageInfo"])
                              ?? FirstCdnUrl(emotion["smallImageInfo"])
                              ?? FirstUrl(emotion["emotionImageBigUrl"])
                              ?? FirstUrl(emotion["emotionImageSmallUrl"]);
                if (string.IsNullOrWhiteSpace(url)) continue;

                list.Add(new EmoteEntry
                {
                    Id = id,
                    Url = NormalizeUrl(url!),
                    Package = packageName,
                    Name = ReadString(emotion["name"]) ?? string.Empty
                });
            }
        }

        return list;
    }

    /// <summary>{ "bigImageInfo": { "thumbnailImage": { "cdnUrls": [ { "url": ... } ] } } }</summary>
    private static string? FirstCdnUrl(JsonNode? info)
    {
        if (info?["thumbnailImage"]?["cdnUrls"] is JsonArray urls) return FirstUrl(urls);
        return null;
    }

    private static string? FirstUrl(JsonNode? arrayNode)
    {
        if (arrayNode is not JsonArray array) return null;

        foreach (var item in array)
        {
            string? url = item is JsonObject obj ? ReadString(obj["url"]) : ReadString(item);
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        return null;
    }

    /// <summary>接口给的是 http，统一升到 https，免得被 WinUI 的图片加载拦下</summary>
    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + url[7..];
        }

        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;

        return url;
    }

    private static List<EmoteEntry> BuildLegacyFallback()
    {
        var list = new List<EmoteEntry>(55);
        for (int i = 1; i <= 55; i++)
        {
            string code = i < 10 ? "0" + i : i.ToString();
            list.Add(new EmoteEntry
            {
                Id = i,
                Url = LegacyAcEmoteBase + code + ".gif",
                Package = "AC娘",
                Name = code
            });
        }

        return list;
    }

    // ───────────────────────────── 缓存 ─────────────────────────────

    private static CachePayload? ReadCache()
    {
        string path = CachePath;
        if (!File.Exists(path)) return null;

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<CachePayload>(stream);
    }

    private static void WriteCache(List<EmoteEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new CachePayload
            {
                SavedAt = DateTimeOffset.UtcNow,
                Entries = entries
            };

            // 先写临时文件再替换，避免半截文件
            string tmp = CachePath + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, payload);
            }

            File.Move(tmp, CachePath, overwrite: true);
        }
        catch
        {
            // 缓存写失败不影响运行
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;

        var raw = node.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim('"');
    }

    private static long ReadLong(JsonNode? node)
    {
        if (node is null) return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out long l)) return l;
            if (value.TryGetValue<double>(out double d)) return (long)d;
            if (value.TryGetValue<string>(out var s) && long.TryParse(s, out long parsed)) return parsed;
        }

        return long.TryParse(node.ToString().Trim('"'), out long fallback) ? fallback : 0;
    }

    private sealed class CachePayload
    {
        public DateTimeOffset SavedAt { get; set; }

        public List<EmoteEntry> Entries { get; set; } = new();
    }
}

/// <summary>一个表情：id + 图片地址 + 所属包</summary>
public sealed class EmoteEntry
{
    public long Id { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Package { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
