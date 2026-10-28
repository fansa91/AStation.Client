using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using AStation_Client.Services;

namespace AStation_Client.Pages;

/// <summary>
/// 直播间播放页。
///
/// 拉流链路（ACFUN_API.md 直播一节，全链路实测）：
///   1. 访问 live.acfun.cn 取 _did Cookie
///   2. POST id.app.acfun.cn/rest/app/visitor/login（sid=acfun.api.visitor）→ 访客令牌
///   3. POST api.kuaishouzt.com/rest/zt/live/web/getPlayUrls（visitorId=主播uid, liveId, pullStreamType=FLV）
///   4. 用 FFmpegInteropX 解 FLV 交给 MediaPlayerElement（HLS 不可用，会返回 129001）
/// </summary>
public sealed partial class LiveRoomPage : Page
{
    private readonly LivePlayService _livePlay = ServiceLocator.LivePlay;

    private string _authorId = string.Empty;
    private string _liveId = string.Empty;
    private LiveStreamResult? _streams;
    private bool _suppressQualityChange;

    public LiveRoomPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not LiveNavArgs args || string.IsNullOrWhiteSpace(args.AuthorId))
        {
            ShowStatus("缺少直播间信息");
            LoadingRing.IsActive = false;
            return;
        }

        _authorId = args.AuthorId;
        _liveId = args.LiveId;

        HeaderTitle.Text = string.IsNullOrWhiteSpace(args.Title) ? "直播间" : args.Title;
        TitleText.Text = string.IsNullOrWhiteSpace(args.Title) ? "未命名直播" : args.Title;
        AuthorText.Text = string.IsNullOrWhiteSpace(args.Author) ? "未知主播" : args.Author;
        OnlineText.Text = string.IsNullOrWhiteSpace(args.OnlineText) ? "在线 --" : $"在线 {args.OnlineText}";
        AuthorIdText.Text = args.AuthorId;
        LiveIdText.Text = string.IsNullOrWhiteSpace(args.LiveId) ? "--" : args.LiveId;

        if (!string.IsNullOrWhiteSpace(args.Category))
        {
            CategoryText.Text = args.Category;
            CategoryBadge.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(args.AuthorAvatar))
        {
            try { Avatar.ProfilePicture = new BitmapImage(new Uri(args.AuthorAvatar)); }
            catch { /* 头像失败不影响拉流 */ }
        }

        await LoadStreamsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Player.MediaPlayer?.Pause();
        Player.Source = null;
    }

    private async Task LoadStreamsAsync()
    {
        ShowStatus("正在获取直播流…");

        _streams = await _livePlay.GetStreamsAsync(_authorId, _liveId);

        if (_streams is null || !_streams.HasStream)
        {
            LoadingRing.IsActive = false;
            ShowStatus(_streams?.ErrorMessage ?? "拿不到直播流地址");
            return;
        }

        StreamNameText.Text = string.IsNullOrWhiteSpace(_streams.StreamName) ? "--" : _streams.StreamName;

        _suppressQualityChange = true;
        QualityBox.ItemsSource = _streams.Qualities;
        QualityBox.SelectedIndex = ResolveQualityIndex(_streams);
        _suppressQualityChange = false;

        await PlayQualityAsync(_streams.Qualities[QualityBox.SelectedIndex]);
    }

    private static int ResolveQualityIndex(LiveStreamResult streams)
    {
        int preferred = AppSettings.Current.LivePreferredBitrate;
        if (preferred > 0)
        {
            int index = streams.Qualities.FindIndex(q => q.Bitrate == preferred);
            if (index >= 0) return index;
        }

        return streams.Qualities.Count - 1; // 默认最高码率
    }

    private async Task PlayQualityAsync(LiveStreamQuality quality)
    {
        ShowStatus($"正在解码 {quality.Name}…");

        var settings = AppSettings.Current;

        try
        {
            Player.Source = await FFmpegPlayback.CreateAsync(quality.Url, settings.LiveHardwareDecode);
            Player.AutoPlay = true;
            Player.MediaPlayer?.Play();

            DecoderText.Text = settings.LiveHardwareDecode
                ? $"FFmpegInteropX · 硬解 · {quality.BitrateText}"
                : $"FFmpegInteropX · 软解 · {quality.BitrateText}";

            HideStatus();
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            ShowStatus($"FLV 解码失败：{ex.Message}\n可尝试在设置里切换直播解码方式");
        }
    }

    private async void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressQualityChange) return;
        if (QualityBox.SelectedItem is LiveStreamQuality quality)
        {
            await PlayQualityAsync(quality);
        }
    }

    private void PlayerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PlayerHost.ActualWidth <= 0) return;

        double target = Math.Round(PlayerHost.ActualWidth * 9 / 16);
        if (Math.Abs(PlayerHost.Height - target) < 1) return;

        PlayerHost.Height = target;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(LivePage));
    }

    private void ShowStatus(string message)
    {
        LoadingRing.IsActive = true;
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private void HideStatus()
    {
        LoadingRing.IsActive = false;
        StatusPanel.Visibility = Visibility.Collapsed;
    }
}
