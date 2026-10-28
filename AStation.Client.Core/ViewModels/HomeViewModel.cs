using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AStation_Client.Models;
using AStation_Client.Services;

namespace AStation_Client.ViewModels;

public class HomeViewModel : INotifyPropertyChanged
{
    private readonly ApiService _api = ServiceLocator.Api;
    private bool _isLoading;
    private string? _errorMessage;

    /// <summary>「换一批」在三个周期之间轮转，拿到的是真正不同的一批数据，而不是本地打乱</summary>
    private static readonly string[] RankPeriods = ["DAY", "THREE_DAYS", "WEEK"];
    private int _periodIndex;

    public ObservableCollection<ChannelItem> Channels { get; } = new();

    /// <summary>首页分区条只放前面几个，剩下的走「全部分区」</summary>
    public ObservableCollection<ChannelItem> QuickChannels { get; } = new();

    public ObservableCollection<VideoItem> Videos { get; } = new();

    private VideoItem? _heroMain;
    private VideoItem? _heroSecond;
    private VideoItem? _heroThird;
    private VideoItem? _heroFourth;

    /// <summary>Hero 主卡（榜一）</summary>
    public VideoItem? HeroMain
    {
        get => _heroMain;
        private set => SetProperty(ref _heroMain, value);
    }

    public VideoItem? HeroSecond
    {
        get => _heroSecond;
        private set => SetProperty(ref _heroSecond, value);
    }

    public VideoItem? HeroThird
    {
        get => _heroThird;
        private set => SetProperty(ref _heroThird, value);
    }

    public VideoItem? HeroFourth
    {
        get => _heroFourth;
        private set => SetProperty(ref _heroFourth, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    /// <summary>
    /// 加载首页数据：频道导航 + 推荐视频（使用排行榜作为推荐源）
    /// </summary>
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // 并行加载频道导航和推荐视频
            await Task.WhenAll(LoadChannelsAsync(), LoadRecommendVideosAsync());
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadChannelsAsync()
    {
        // 调用 AcFun 导航树接口：GET /rest/pc-direct/page/queryNavigators
        // 返回频道导航的正式数据源
        var response = await _api.GetAsync<NavigatorsResponse>("/rest/pc-direct/page/queryNavigators");

        if (response?.Result != 0 || response.Data == null)
        {
            // 接口失败时回退到默认频道
            LoadDefaultChannels();
            return;
        }

        Channels.Clear();
        foreach (var nav in response.Data)
        {
            // 只取一级导航（parent 为空或根节点），过滤掉没有链接的节点
            if (string.IsNullOrWhiteSpace(nav.NavName)) continue;
            Channels.Add(new ChannelItem
            {
                Name = nav.NavName,
                Tag = nav.Link ?? nav.Cid.ToString(),
                Id = nav.Cid
            });
        }

        // 如果没有解析到任何频道，回退默认
        if (Channels.Count == 0)
        {
            LoadDefaultChannels();
        }

        RebuildQuickChannels();
    }

    /// <summary>分区条只放前 6 个常用分区，其余走「全部分区」，避免首页横向拉成一条长龙</summary>
    private void RebuildQuickChannels()
    {
        QuickChannels.Clear();
        foreach (var channel in Channels.Take(6))
        {
            QuickChannels.Add(channel);
        }
    }

    private async Task LoadRecommendVideosAsync(string? period = null)
    {
        // 调用排行榜接口作为推荐视频源：GET /rest/pc-direct/rank/channel
        // 参数：channelId 不传或为空，rankPeriod=DAY，rankLimit=28
        var query = new Dictionary<string, string>
        {
            ["rankLimit"] = "28",
            ["rankPeriod"] = period ?? RankPeriods[_periodIndex]
        };

        var response = await _api.GetAsync<RankResponse>("/rest/pc-direct/rank/channel", query);

        if (response?.Result != 0 || response.RankList == null)
        {
            LoadDefaultVideos();
            return;
        }

        var items = new List<VideoItem>(response.RankList.Count);
        foreach (var item in response.RankList)
        {
            var contentId = item.ContentId ?? item.DougaId;
            items.Add(new VideoItem
            {
                Title = item.Title ?? "未知标题",
                Author = item.UserName ?? item.UserId.ToString(),
                ViewCount = FormatCount(item.ViewCount),
                Duration = FormatDuration(item.DurationMs),
                Thumbnail = item.Cover ?? string.Empty,
                ResourceId = contentId?.ToString() ?? string.Empty
            });
        }

        if (items.Count == 0)
        {
            LoadDefaultVideos();
            return;
        }

        // 前 4 条进 Hero（1 大 3 小），其余进推荐网格 —— 两处不重复
        HeroMain = items.ElementAtOrDefault(0);
        HeroSecond = items.ElementAtOrDefault(1);
        HeroThird = items.ElementAtOrDefault(2);
        HeroFourth = items.ElementAtOrDefault(3);

        Videos.Clear();
        foreach (var item in items.Skip(4))
        {
            Videos.Add(item);
        }
    }

    /// <summary>换一批：轮转到下一个周期，Hero 与推荐网格一起换</summary>
    public async Task RefreshRecommendAsync()
    {
        _periodIndex = (_periodIndex + 1) % RankPeriods.Length;

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await LoadRecommendVideosAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>毫秒时长 -> mm:ss / hh:mm:ss</summary>
    internal static string FormatDuration(long? durationMs)
    {
        if (durationMs is null or <= 0) return "--:--";

        var ts = TimeSpan.FromMilliseconds(durationMs.Value);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>播放量格式化：>=10000 显示 x.x万</summary>
    internal static string FormatCount(long? count)
    {
        if (count is null or <= 0) return "0";
        if (count >= 100_000_000) return $"{count.Value / 100_000_000.0:0.#}亿";
        if (count >= 10_000) return $"{count.Value / 10_000.0:0.#}万";
        return count.Value.ToString();
    }

    private void LoadDefaultChannels()
    {
        Channels.Clear();
        Channels.Add(new ChannelItem { Name = "综合", Tag = "home", Id = 1 });
        Channels.Add(new ChannelItem { Name = "游戏", Tag = "game", Id = 2 });
        Channels.Add(new ChannelItem { Name = "舞蹈", Tag = "dance", Id = 3 });
        Channels.Add(new ChannelItem { Name = "音乐", Tag = "music", Id = 4 });
        Channels.Add(new ChannelItem { Name = "科技", Tag = "tech", Id = 5 });
        Channels.Add(new ChannelItem { Name = "生活", Tag = "life", Id = 6 });
        Channels.Add(new ChannelItem { Name = "影视", Tag = "film", Id = 7 });
        Channels.Add(new ChannelItem { Name = "体育", Tag = "sport", Id = 8 });
        RebuildQuickChannels();
    }

    private void LoadDefaultVideos()
    {
        Videos.Clear();
        HeroMain = new VideoItem
        {
            Title = "WinUI3 桌面客户端开发入门教程",
            Author = "AStation 开发者",
            ViewCount = "1.2万",
            Duration = "15:30",
            Thumbnail = "ms-appx:///Assets/StoreLogo.png"
        };
        HeroSecond = null;
        HeroThird = null;
        HeroFourth = null;

        Videos.Add(new VideoItem
        {
            Title = "WinUI3 桌面客户端开发入门教程",
            Author = "AStation 开发者",
            ViewCount = "1.2万",
            Duration = "15:30",
            Thumbnail = "ms-appx:///Assets/StoreLogo.png"
        });
    }
}

/// <summary>
/// 导航树接口响应模型：GET /rest/pc-direct/page/queryNavigators
/// 返回 {result:0, code:200, data:[{id, navName, parent, link, cid, ...}]}
/// </summary>
public class NavigatorsResponse
{
    public int Result { get; set; }
    public int Code { get; set; }
    public List<NavigatorItem> Data { get; set; } = new();
}

public class NavigatorItem
{
    public int Id { get; set; }
    public string? NavName { get; set; }
    public int? Parent { get; set; }
    public string? Link { get; set; }
    public int Cid { get; set; }
}

/// <summary>
/// 排行榜接口响应模型：GET /rest/pc-direct/rank/channel
/// </summary>
public class RankResponse
{
    public int Result { get; set; }
    public List<RankVideoItem> RankList { get; set; } = new();
}

public class RankVideoItem
{
    /// <summary>接口字段为 contentTitle（并非 title）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contentTitle")]
    public string? Title { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("userId")]
    public long UserId { get; set; }

    /// <summary>接口字段为 viewCount（并非 playCount）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("viewCount")]
    public long? ViewCount { get; set; }

    /// <summary>缩略图字段为 coverUrl / videoCover（并非 cover）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("coverUrl")]
    public string? Cover { get; set; }

    /// <summary>时长，单位毫秒</summary>
    [System.Text.Json.Serialization.JsonPropertyName("duration")]
    public long? DurationMs { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("contentId")]
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
    public long? ContentId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("dougaId")]
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
    public long? DougaId { get; set; }
}
