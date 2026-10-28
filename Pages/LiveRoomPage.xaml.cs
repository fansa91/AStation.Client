using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    // ── 响应式断点：与点播页保持一致 ──
    private const double BreakpointStack = 1040;
    private const double BreakpointSideWide = 1280;

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

        // 弹幕分页：通道未接入，用统一状态面板说明，不再是几段散文字
        DanmakuState.ShowEmpty(
            "直播弹幕通道尚未接入",
            "弹幕层与开关已经就位，等快手 zt.live 的 protobuf 长连接接入后可直接复用播放器内的弹幕层。",
            glyph: "\uE90A");

        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth, ActualHeight);
    }

    // ───────────────────────────── 响应式 ─────────────────────────────

    private bool _stacked;

    private static Thickness PagePaddingFor(double width)
        => width < 900 ? new Thickness(16, 16, 16, 20) : new Thickness(24, 20, 24, 24);

    /// <summary>与点播页同构：宽屏并排、窄屏堆叠、全屏单独处理</summary>
    private void ApplyResponsiveLayout(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        if (Overlay.IsFullscreen)
        {
            Overlay.MaxWindowedHeight = 0;
            Overlay.MaxWidth = double.PositiveInfinity;
            Overlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        LayoutGrid.Padding = PagePaddingFor(width);

        bool stacked = width < BreakpointStack;
        _stacked = stacked;

        if (stacked)
        {
            SideColumn.Width = new GridLength(0);
            HostSidePanel(stacked: true);

            BodyScroll.VerticalScrollMode = ScrollMode.Enabled;
            BodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            BodyGrid.RowDefinitions[0].Height = GridLength.Auto;
            StackedRow.Height = GridLength.Auto;

            Grid.SetRowSpan(MainColumn, 1);
            SidePanel.Height = 320;
        }
        else
        {
            SideColumn.Width = new GridLength(width >= BreakpointSideWide ? 380 : 340);
            HostSidePanel(stacked: false);

            BodyScroll.VerticalScrollMode = ScrollMode.Disabled;
            BodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

            BodyGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            StackedRow.Height = new GridLength(0);

            Grid.SetRowSpan(MainColumn, 1);
            SidePanel.Height = double.NaN;
        }

        // 与点播页同一套算式：宽度由左列决定（正常就是 16:9），这里只是超宽矮窗口的安全阀；
        // 堆叠模式下方能滚，上限放宽，优先保画面大小
        var padding = PagePaddingFor(width);
        double available = height - padding.Top - padding.Bottom - 12;
        double maxPlayerHeight = Math.Max(180, Math.Round(available * (stacked ? 0.82 : 0.68)));
        Overlay.MaxWindowedHeight = maxPlayerHeight;
        Overlay.MaxWidth = Math.Round(maxPlayerHeight * Overlay.AspectRatio);
        Overlay.HorizontalAlignment = HorizontalAlignment.Center;
    }

    /// <summary>
    /// 与点播页相同的侧栏宿主切换：宽屏挂在 LayoutGrid 右列跨两行，窄屏挂到内容流最下方。
    /// </summary>
    private void HostSidePanel(bool stacked)
    {
        if (stacked)
        {
            if (ReferenceEquals(SidePanel.Parent, BodyGrid)) return;

            LayoutGrid.Children.Remove(SidePanel);
            Grid.SetColumn(SidePanel, 0);
            Grid.SetRow(SidePanel, 1);
            Grid.SetColumnSpan(SidePanel, 1);
            Grid.SetRowSpan(SidePanel, 1);
            BodyGrid.Children.Add(SidePanel);
        }
        else
        {
            if (ReferenceEquals(SidePanel.Parent, LayoutGrid)) return;

            BodyGrid.Children.Remove(SidePanel);
            Grid.SetColumn(SidePanel, 1);
            Grid.SetRow(SidePanel, 0);
            Grid.SetColumnSpan(SidePanel, 1);
            Grid.SetRowSpan(SidePanel, 2);
            LayoutGrid.Children.Add(SidePanel);
        }
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
        UidText.Text = $"UID {args.AuthorId}";
        OnlineText.Text = online;
        CategoryText.Text = category;
        LiveIdText.Text = string.IsNullOrWhiteSpace(args.LiveId) ? "--" : args.LiveId;

        if (!string.IsNullOrWhiteSpace(args.AuthorAvatar))
        {
            try
            {
                Avatar.ProfilePicture = new BitmapImage(new Uri(args.AuthorAvatar));
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
        LineState.ShowLoading("正在获取码率档位…");

        _streams = await _livePlay.GetStreamsAsync(_authorId, _liveId);

        if (_streams is null || !_streams.HasStream)
        {
            string message = _streams?.ErrorMessage ?? "拿不到直播流地址";
            Overlay.ShowStatus(message, spinner: false);
            LineState.ShowError("拉流失败", message, "重试");
            return;
        }

        StreamNameText.Text = string.IsNullOrWhiteSpace(_streams.StreamName) ? "--" : _streams.StreamName;
        ListCountText.Text = $"{_streams.Qualities.Count} 档";

        int index = ResolveQualityIndex(_streams);

        _suppressQualityChange = true;
        QualityList.ItemsSource = _streams.Qualities;
        _flyoutQualityList.ItemsSource = _streams.Qualities;
        QualityList.SelectedIndex = index;
        _flyoutQualityList.SelectedIndex = index;
        _suppressQualityChange = false;

        Overlay.SetQualityText(_streams.Qualities[index].Name);
        LineState.Hide();

        await PlayQualityAsync(_streams.Qualities[index]);
    }

    private async void LineState_ActionClick(object sender, EventArgs e)
    {
        await LoadStreamsAsync();
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

    /// <summary>全屏时内容行高度归零，直播画面铺满；退出时按当前宽度重算</summary>
    private void Overlay_FullscreenChanged(object? sender, bool fullscreen)
    {
        if (fullscreen)
        {
            LayoutGrid.Padding = new Thickness(0);
            LayoutGrid.RowSpacing = 0;
            LayoutGrid.ColumnSpacing = 0;
            SideColumn.Width = new GridLength(0);
            SidePanel.Visibility = Visibility.Collapsed;
            // 窗口模式给播放器设过 MaxWidth（居中收窄），全屏必须还原成铺满
            Overlay.MaxWidth = double.PositiveInfinity;
            Overlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            PlayerRow.Height = new GridLength(1, GridUnitType.Star);
            ContentRow.Height = new GridLength(0);
            BodyScroll.Visibility = Visibility.Collapsed;
            FullscreenBackdrop.Visibility = Visibility.Visible;
        }
        else
        {
            LayoutGrid.RowSpacing = 12;
            LayoutGrid.ColumnSpacing = 18;
            SidePanel.Visibility = Visibility.Visible;
            PlayerRow.Height = GridLength.Auto;
            ContentRow.Height = new GridLength(1, GridUnitType.Star);
            BodyScroll.Visibility = Visibility.Visible;
            FullscreenBackdrop.Visibility = Visibility.Collapsed;

            ApplyResponsiveLayout(ActualWidth, ActualHeight);
        }
    }

    private void GoBack()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(LivePage));
    }

    // ───────────────────────────── 右栏切换（清晰度 / 弹幕） ─────────────────────────────

    private void SideTab_Click(object sender, RoutedEventArgs e)
    {
        SelectSideTab(ReferenceEquals(sender, DanmakuTab) ? 1 : 0);
    }

    private void SelectSideTab(int index)
    {
        LineTab.IsChecked = index == 0;
        DanmakuTab.IsChecked = index == 1;

        LinePanelContent.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DanmakuPanelContent.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;

        ListCountText.Text = index == 0 && _streams is { HasStream: true }
            ? $"{_streams.Qualities.Count} 档"
            : string.Empty;
    }
}
