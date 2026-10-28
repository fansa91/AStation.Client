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

    public ObservableCollection<ChannelItem> Channels { get; } = new();
    public ObservableCollection<VideoItem> Videos { get; } = new();

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
                Tag = nav.Link ?? nav.Cid.ToString()
            });
        }

        // 如果没有解析到任何频道，回退默认
        if (Channels.Count == 0)
        {
            LoadDefaultChannels();
        }
    }

    private async Task LoadRecommendVideosAsync()
    {
        // 调用排行榜接口作为推荐视频源：GET /rest/pc-direct/rank/channel
        // 参数：channelId 不传或为空，rankPeriod=DAY，rankLimit=20
        var query = new Dictionary<string, string>
        {
            ["rankLimit"] = "20",
            ["rankPeriod"] = "DAY"
        };

        var response = await _api.GetAsync<RankResponse>("/rest/pc-direct/rank/channel", query);

        if (response?.Result != 0 || response.RankList == null)
        {
            LoadDefaultVideos();
            return;
        }

        Videos.Clear();
        foreach (var item in response.RankList)
        {
            var contentId = item.ContentId ?? item.DougaId;
            Videos.Add(new VideoItem
            {
                Title = item.Title ?? "未知标题",
                Author = item.UserName ?? item.UserId.ToString(),
                ViewCount = FormatCount(item.ViewCount),
                Duration = FormatDuration(item.DurationMs),
                Thumbnail = item.Cover ?? string.Empty,
                ResourceId = contentId?.ToString() ?? string.Empty
            });
        }

        if (Videos.Count == 0)
        {
            LoadDefaultVideos();
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
        Channels.Add(new ChannelItem { Name = "综合", Tag = "home" });
        Channels.Add(new ChannelItem { Name = "游戏", Tag = "game" });
        Channels.Add(new ChannelItem { Name = "舞蹈", Tag = "dance" });
        Channels.Add(new ChannelItem { Name = "音乐", Tag = "music" });
        Channels.Add(new ChannelItem { Name = "科技", Tag = "tech" });
        Channels.Add(new ChannelItem { Name = "生活", Tag = "life" });
        Channels.Add(new ChannelItem { Name = "影视", Tag = "film" });
        Channels.Add(new ChannelItem { Name = "体育", Tag = "sport" });
    }

    private void LoadDefaultVideos()
    {
        Videos.Clear();
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
