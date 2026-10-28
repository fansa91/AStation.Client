using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
///
/// 播放器外壳与点播页共用 Controls.PlayerOverlay（直播形态：无进度条 / 无倍速，
/// 保留音量、清晰度档位、弹幕开关与全屏），本页只负责拉流与房间信息。
/// </summary>
public sealed partial class LiveRoomPage : Page
{
    private readonly LivePlayService _livePlay = ServiceLocator.LivePlay;

    private string _authorId = string.Empty;
    private string _liveId = string.Empty;
    private LiveStreamResult? _streams;
    private bool _suppressQualityChange;

    /// <summary>控制栏「清晰度」Flyout 里的码率列表（与侧栏列表共用同一份数据）</summary>
    private ListView _flyoutQualityList = null!;

    private Brush? _savedPageBackground;

    public LiveRoomPage()
    {
        InitializeComponent();

        // 直播形态：控制条单行、无进度、无倍速，右上角常显 LIVE 标识
        Overlay.ConfigureForLive();
        Overlay.SetLineVisible(false);
        Overlay.SetDanmakuAvailable(false, "直播弹幕通道尚未接入，开关暂不可用");

        BuildQualityPanels();

        Overlay.BackRequested += (_, _) => GoBack();
        Overlay.FullscreenChanged += Overlay_FullscreenChanged;
        Overlay.MediaFailed += Overlay_MediaFailed;

        _savedPageBackground = PageRoot.Background;
    }

    // ───────────────────────────── 清晰度 / 线路 ─────────────────────────────

    private void BuildQualityPanels()
    {
        var template = (DataTemplate)Resources["LiveQualityTemplate"];

        QualityList.ItemTemplate = template;
        QualityList.SelectionChanged += Quality_SelectionChanged;

        _flyoutQualityList = new ListView
        {
            MinWidth = 220,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = template,
        };
        _flyoutQualityList.SelectionChanged += Quality_SelectionChanged;

        Overlay.AttachQualityContent(_flyoutQualityList);
    }

    private async void Quality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressQualityChange) return;
        if (sender is not ListView list) return;
        if (list.SelectedItem is not LiveStreamQuality quality) return;

        SyncQualitySelection(list);
        await PlayQualityAsync(quality);
    }

    /// <summary>侧栏列表与控制栏 Flyout 互相同步选中项</summary>
    private void SyncQualitySelection(ListView source)
    {
        _suppressQualityChange = true;

        var item = source.SelectedItem;
        if (!ReferenceEquals(source, QualityList)) QualityList.SelectedItem = item;
        if (!ReferenceEquals(source, _flyoutQualityList)) _flyoutQualityList.SelectedItem = item;

        if (item is LiveStreamQuality quality) Overlay.SetQualityText(quality.Name);

        _suppressQualityChange = false;
    }

    // ───────────────────────────── 生命周期 ─────────────────────────────

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not LiveNavArgs args || string.IsNullOrWhiteSpace(args.AuthorId))
        {
            Overlay.ShowStatus("缺少直播间信息", spinner: false);
            return;
        }

        _authorId = args.AuthorId;
        _liveId = args.LiveId;

        string title = string.IsNullOrWhiteSpace(args.Title) ? "未命名直播" : args.Title;
        string author = string.IsNullOrWhiteSpace(args.Author) ? "未知主播" : args.Author;
        string online = string.IsNullOrWhiteSpace(args.OnlineText) ? "在线 --" : $"在线 {args.OnlineText}";
        string category = string.IsNullOrWhiteSpace(args.Category) ? "分区 --" : args.Category;

        // 播放器内的标题条 / 左信息位
        Overlay.SetTitle(title, $"{author} · {online}", "直播中");

        TitleText.Text = title;
        AuthorText.Text = author;
        SideAuthorText.Text = author;
        OnlineText.Text = online;
        SideOnlineText.Text = online.Replace("在线 ", string.Empty);
        CategoryText.Text = category;
        SideCategoryText.Text = category;
        MetaText.Text = $"{category} · {online}";
        LiveIdText.Text = string.IsNullOrWhiteSpace(args.LiveId) ? "--" : args.LiveId;
        SideUidText.Text = $"UID {args.AuthorId}";

        if (!string.IsNullOrWhiteSpace(args.AuthorAvatar))
        {
            try
            {
                var avatar = new BitmapImage(new Uri(args.AuthorAvatar));
                Avatar.ProfilePicture = avatar;
                SideAvatar.ProfilePicture = avatar;
            }
            catch { /* 头像失败不影响拉流 */ }
        }

        await LoadStreamsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        var mp = Overlay.MediaElement.MediaPlayer;
        mp?.Pause();
        Overlay.MediaElement.Source = null;

        // 离开页面时务必还原窗口呈现方式，否则会卡在全屏
        Overlay.ExitFullscreen();
    }

    // ───────────────────────────── 拉流 ─────────────────────────────

    private async Task LoadStreamsAsync()
    {
        Overlay.ShowStatus("正在获取直播流…");

        _streams = await _livePlay.GetStreamsAsync(_authorId, _liveId);

        if (_streams is null || !_streams.HasStream)
        {
            Overlay.ShowStatus(_streams?.ErrorMessage ?? "拿不到直播流地址", spinner: false);
            SideEmptyHint.Text = _streams?.ErrorMessage ?? "拿不到直播流地址";
            SideEmptyHint.Visibility = Visibility.Visible;
            return;
        }

        StreamNameText.Text = string.IsNullOrWhiteSpace(_streams.StreamName) ? "--" : _streams.StreamName;
        SideStreamText.Text = StreamNameText.Text;
        ListCountText.Text = $"{_streams.Qualities.Count} 档";

        int index = ResolveQualityIndex(_streams);

        _suppressQualityChange = true;
        QualityList.ItemsSource = _streams.Qualities;
        _flyoutQualityList.ItemsSource = _streams.Qualities;
        QualityList.SelectedIndex = index;
        _flyoutQualityList.SelectedIndex = index;
        _suppressQualityChange = false;

        Overlay.SetQualityText(_streams.Qualities[index].Name);

        await PlayQualityAsync(_streams.Qualities[index]);
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
        Overlay.ShowStatus($"正在解码 {quality.Name}…");

        var settings = AppSettings.Current;

        try
        {
            Overlay.MediaElement.Source = await FFmpegPlayback.CreateAsync(quality.Url, settings.LiveHardwareDecode);
            Overlay.MediaElement.AutoPlay = true;
            Overlay.MediaElement.MediaPlayer?.Play();

            DecoderText.Text = settings.LiveHardwareDecode
                ? $"FFmpegInteropX · 硬解 · {quality.BitrateText}"
                : $"FFmpegInteropX · 软解 · {quality.BitrateText}";
        }
        catch (Exception ex)
        {
            Overlay.ShowStatus($"FLV 解码失败：{ex.Message}\n可尝试在设置里切换直播解码方式", spinner: false);
        }
    }

    private void Overlay_MediaFailed(object? sender, string message)
    {
        Overlay.ShowStatus($"直播流播放失败：{message}\n可尝试切换清晰度档位或解码方式", spinner: false);
    }

    // ───────────────────────────── 全屏 / 返回 ─────────────────────────────

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Overlay.IsFullscreen) return;

        Overlay.ExitFullscreen();
        args.Handled = true;
    }

    /// <summary>全屏时收起右栏与房间信息，让直播画面真正铺满</summary>
    private void Overlay_FullscreenChanged(object? sender, bool fullscreen)
    {
        if (fullscreen)
        {
            LayoutGrid.Padding = new Thickness(0);
            LayoutGrid.ColumnSpacing = 0;
            SideColumn.Width = new GridLength(0);
            SidePanel.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            RootScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RootScroll.VerticalScrollMode = ScrollMode.Disabled;

            PageRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        }
        else
        {
            LayoutGrid.Padding = new Thickness(20, 4, 20, 24);
            LayoutGrid.ColumnSpacing = 18;
            SideColumn.Width = new GridLength(340);
            SidePanel.Visibility = Visibility.Visible;
            InfoPanel.Visibility = Visibility.Visible;
            RootScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            RootScroll.VerticalScrollMode = ScrollMode.Enabled;

            PageRoot.Background = _savedPageBackground ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void GoBack()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(LivePage));
    }

    // ───────────────────────────── 右栏切换 ─────────────────────────────

    private void SideTab_Click(object sender, RoutedEventArgs e)
    {
        int index = ReferenceEquals(sender, LineTab) ? 1
            : ReferenceEquals(sender, DanmakuTab) ? 2
            : 0;

        SelectSideTab(index);
    }

    private void SelectSideTab(int index)
    {
        InfoTab.IsChecked = index == 0;
        LineTab.IsChecked = index == 1;
        DanmakuTab.IsChecked = index == 2;

        InfoPanelContent.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        LinePanelContent.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        DanmakuPanelContent.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        ListTitle.Text = index switch
        {
            1 => "清晰度 / 线路",
            2 => "弹幕",
            _ => "直播信息"
        };

        ListCountText.Text = index switch
        {
            1 => _streams is { HasStream: true } ? $"{_streams.Qualities.Count} 档" : string.Empty,
            _ => string.Empty
        };
    }
}
