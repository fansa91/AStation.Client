using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using AStation_Client.Services;
using Windows.System;

namespace AStation_Client.Pages;

/// <summary>
/// 视频 / 番剧播放页。
///
/// 数据链路（全部按 ACFUN_API.md 实测接口实现）：
///   · 视频页 /v/ac{dougaId} SSR → window.videoInfo（标题、UP主、播放/弹幕/评论/点赞/收藏、分P、channel.id）
///   · 番剧页 /bangumi/aa{bangumiId} SSR → window.bangumiData（播放量、追番数、mkey、当前集）+ 集列表
///   · 取流 GET /rest/pc-direct/play/playInfo/ksPlayJson?resourceId=&amp;resourceType=
///     ksPlayJson / ksPlayJsonHevc 是「字符串化的 JSON」，二次解析后才有清晰度档位
///     每档 representation 带 url（腾讯云）+ backupUrl[]（阿里云…）→ 界面上的「线路」下拉
///   · 弹幕 POST /rest/pc-direct/new-danmaku/list（resourceId = 分P videoId / 集 videoId，resourceType=9）
///   · 评论 GET /rest/pc-direct/comment/list（sourceId + sourceType=3）
///   · 相关视频 POST /rest/pc-direct/channel/query/match（同频道内容流）
///
/// 播放器外壳（视频区 / 控制栏 / 顶栏 / 全屏 / 自动隐藏）已抽到 Controls.PlayerOverlay，
/// 与直播页共用一套；本页只负责取流、弹幕数据与右侧信息面板。
/// </summary>
public sealed partial class VideoPage : Page
{
    private const int CommentPageSize = 50;

    private readonly PlayService _play = ServiceLocator.Play;
    private readonly DanmakuService _danmaku = ServiceLocator.Danmaku;
    private readonly VideoDetailService _videoDetail = ServiceLocator.VideoDetail;
    private readonly BangumiService _bangumi = ServiceLocator.Bangumi;
    private readonly CommentService _comment = ServiceLocator.Comment;
    private readonly RelatedService _related = ServiceLocator.Related;

    private readonly DispatcherTimer _danmakuTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly List<ClientDanmaku> _danmakus = new();

    private int _danmakuCursor;
    private double _lastPosition = -1;
    private long _pendingResumeSeconds;

    /// <summary>本次 MediaOpened 是「换清晰度/线路」触发的，必须回到原来的位置（不受续播开关影响）</summary>
    private bool _resumeAfterSourceSwitch;
    private bool _suppressQualityChange;
    private bool _suppressCdnChange;
    private bool _ffmpegFallbackTried;
    private string? _currentUrl;
    private bool _isBangumi;
    private string _resourceId = string.Empty;
    private string _currentVideoId = string.Empty;
    private string? _mkey;
    private string _channelId = string.Empty;

    private int _commentPage;
    private int _commentTotalPage;
    private long _commentTotalCount;
    private bool _commentsLoaded;

    /// <summary>清晰度 / 线路两个 Flyout 的列表实例（内容挂在共用播放器控件里）</summary>
    private ListView _qualityList = null!;
    private ListView _cdnList = null!;

    /// <summary>分P/选集区的空状态文案（列表为空时显示）</summary>
    private string? _playlistHint;

    /// <summary>分P/选集分页的标题（切到相关后切回来要还原）</summary>
    private string _playlistTitle = "分P";

    /// <summary>简介是否已展开（超过两行时才显示切换按钮）</summary>
    private bool _descriptionExpanded;

    /// <summary>番剧元信息的固定部分（不含「当前第N集」）</summary>
    private List<string> _bangumiMetaBase = new();

    public ObservableCollection<PlaylistEntry> Playlist { get; } = new();
    public ObservableCollection<CommentItem> Comments { get; } = new();
    public ObservableCollection<RelatedVideoItem> RelatedVideos { get; } = new();

    public VideoPage()
    {
        InitializeComponent();

        _danmakuTimer.Tick += DanmakuTimer_Tick;

        // 播放器外壳：点播形态 + 两个 Flyout 列表
        Overlay.ConfigureForVideo();
        BuildQualityPanel();
        BuildLinePanel();

        Overlay.MediaOpened += Overlay_MediaOpened;
        Overlay.MediaFailed += Overlay_MediaFailed;
        Overlay.Seeked += (_, seconds) => ResyncDanmakuCursor(seconds);
        Overlay.PrevRequested += async (_, _) => await PlayRelativeAsync(-1);
        Overlay.NextRequested += async (_, _) => await PlayRelativeAsync(1);
        Overlay.BackRequested += (_, _) => GoBack();
        Overlay.FullscreenChanged += Overlay_FullscreenChanged;
        Overlay.DanmakuToggled += Overlay_DanmakuToggled;

        EpisodeList.ItemsSource = Playlist;
        CommentList.ItemsSource = Comments;
        RelatedList.ItemsSource = RelatedVideos;

        // 两列布局按宽度切换：宽屏并排、窄屏堆叠；同时给播放器算一个高度上限
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth, ActualHeight);
    }

    // ───────────────────────────── 响应式 ─────────────────────────────

    /// <summary>低于这个宽度，右侧「选集 / 相关」折到内容下方，页面整体滚动</summary>
    private const double BreakpointStack = 1040;

    /// <summary>宽屏时侧栏加到 380，一屏能多放几条</summary>
    private const double BreakpointSideWide = 1280;

    private bool _stacked;

    private static Thickness PagePaddingFor(double width)
        => width < 900 ? new Thickness(16, 16, 16, 20) : new Thickness(24, 20, 24, 24);

    /// <summary>
    /// 播放页三态：
    ///   宽（≥1280）  播放器 + 下方评论（左列）/ 选集·相关 380 侧栏（右列，贯通播放器与评论）
    ///   中（1040+）  同上，侧栏 340
    ///   窄（<1040）  侧栏折到内容最下方，页面整体滚动，列表给固定高度
    /// 全屏由 <see cref="Overlay_FullscreenChanged"/> 单独处理。
    /// </summary>
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
            SidePanel.Height = 340;

            // 页面整体滚动时，评论列表自己给个高度，避免 50 条评论把页面拉到很长
            CommentList.MaxHeight = 420;
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

            CommentList.MaxHeight = double.PositiveInfinity;
        }

        // 播放器尺寸：宽度由左列决定，正常情况下就是 16:9，这里只是「超宽矮窗口」的安全阀 ——
        // 高度超了就按比例收窄宽度并居中（硬压高度会让画面左右长黑边）。
        // 堆叠模式下方内容本来就能滚，所以上限放宽，优先保画面大小。
        var padding = PagePaddingFor(width);
        double available = height - padding.Top - padding.Bottom - 12;
        double maxPlayerHeight = Math.Max(180, Math.Round(available * (stacked ? 0.82 : 0.68)));
        Overlay.MaxWindowedHeight = maxPlayerHeight;
        Overlay.MaxWidth = Math.Round(maxPlayerHeight * Overlay.AspectRatio);
        Overlay.HorizontalAlignment = HorizontalAlignment.Center;
    }

    /// <summary>
    /// 侧栏的宿主切换：
    ///   宽屏 → 挂在 LayoutGrid 第 1 列、跨播放器行与内容行（与播放器等高的整条侧栏）
    ///   窄屏 → 挂到 BodyGrid 第 1 行（评论下方），跟着页面一起滚
    /// Grid 子元素不能在两处同时存在，所以这里是搬移而不是隐藏。
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

    // ───────────────────────────── 播放器挂载 ─────────────────────────────

    private void BuildQualityPanel()
    {
        _qualityList = new ListView
        {
            MinWidth = 200,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = (DataTemplate)Resources["PlayQualityTemplate"],
        };
        _qualityList.SelectionChanged += QualityBox_SelectionChanged;
        Overlay.AttachQualityContent(_qualityList);
    }

    private void BuildLinePanel()
    {
        _cdnList = new ListView
        {
            MinWidth = 200,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = (DataTemplate)Resources["CdnSourceTemplate"],
        };
        _cdnList.SelectionChanged += CdnBox_SelectionChanged;
        Overlay.AttachLineContent(_cdnList);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        switch (e.Parameter)
        {
            case VideoNavArgs args when !string.IsNullOrWhiteSpace(args.ResourceId):
                await LoadAsync(args.ResourceId, args.ResourceType, args.Title, args.VideoId, args.Mkey);
                break;

            case string resourceId when !string.IsNullOrWhiteSpace(resourceId):
                await LoadAsync(resourceId, 2, null, null, null);
                break;

            default:
                Overlay.ShowStatus("没有可播放的内容", spinner: false);
                break;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _danmakuTimer.Stop();
        Overlay.Danmaku.ClearAll();

        var mp = Overlay.MediaElement.MediaPlayer;
        mp?.Pause();
        Overlay.MediaElement.Source = null;

        // 离开页面时务必还原窗口呈现方式，否则会卡在全屏
        Overlay.ExitFullscreen();
    }

    // ───────────────────────────── 加载 ─────────────────────────────

    private async Task LoadAsync(string resourceId, int resourceType, string? title, string? videoId, string? mkey)
    {
        _resourceId = resourceId;
        _isBangumi = resourceType == 1;
        _mkey = mkey;
        _currentVideoId = videoId ?? string.Empty;

        _channelId = string.Empty;
        _commentPage = 0;
        _commentsLoaded = false;
        Comments.Clear();
        RelatedVideos.Clear();
        LoadMoreCommentButton.Visibility = Visibility.Collapsed;
        CommentCountText.Text = string.Empty;
        CommentState.ShowLoading("正在加载评论…");

        Playlist.Clear();
        _playlistHint = null;
        _playlistTitle = _isBangumi ? "选集" : "分P";
        Overlay.ShowStatus("正在加载播放数据…");
        SelectSideTab(0);

        try
        {
            if (_isBangumi)
            {
                await LoadBangumiInfoAsync(resourceId, title);
            }
            else
            {
                await LoadVideoInfoAsync(resourceId, title);
            }
        }
        catch (Exception ex)
        {
            Overlay.ShowStatus($"读取页面信息失败：{ex.Message}", spinner: false);
        }

        await PlayCurrentAsync();
        UpdateEpisodeButtons();

        // 列表已填充，刷新右栏空状态与计数（否则会残留"没有分P信息"压在列表上）
        SelectSideTab(0);

        // 评论和相关视频与播放并行拉，不阻塞播放
        _ = LoadCommentsAsync(append: false);
        _ = LoadRelatedAsync();
    }

    private async Task LoadVideoInfoAsync(string dougaId, string? title)
    {
        var detail = await _videoDetail.GetVideoDetailAsync(dougaId);

        if (detail is null)
        {
            TitleText.Text = string.IsNullOrWhiteSpace(title) ? $"ac{dougaId}" : title!;
            MetaText.Text = "未能读取到视频详情（接口可能调整）";
            _playlistHint = "没有分P信息";
            _playlistTitle = "分P";
            PlaylistTab.Content = _playlistTitle;
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? detail.Title : title!;
        DescriptionText.Text = string.IsNullOrWhiteSpace(detail.Description) ? "暂无简介" : detail.Description;
        UpdateDescriptionToggle();
        AuthorText.Text = string.IsNullOrWhiteSpace(detail.AuthorName) ? "未知 UP 主" : detail.AuthorName;

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(detail.ChannelName)) meta.Add(detail.ChannelName);
        meta.Add($"{VideoDetailService.FormatCount(detail.ViewCount)} 播放");
        meta.Add($"{VideoDetailService.FormatCount(detail.DanmakuCount)} 弹幕");
        meta.Add($"{VideoDetailService.FormatCount(detail.CommentCount)} 评论");
        if (detail.AuthorFans > 0) meta.Add($"{VideoDetailService.FormatCount(detail.AuthorFans)} 粉丝");
        MetaText.Text = string.Join(" · ", meta);

        LikeText.Text = VideoDetailService.FormatCount(detail.LikeCount);
        FavoriteText.Text = VideoDetailService.FormatCount(detail.StowCount);

        if (!string.IsNullOrWhiteSpace(detail.AuthorAvatar))
        {
            try { AuthorAvatar.ProfilePicture = new BitmapImage(new Uri(detail.AuthorAvatar)); }
            catch { /* 头像加载失败不影响播放 */ }
        }

        _channelId = detail.ChannelId;

        // 分P 列表
        _playlistTitle = detail.Parts.Count > 1 ? $"分P（共 {detail.Parts.Count} 个）" : "分P";
        PlaylistTab.Content = "分P";
        ListCountText.Text = detail.Parts.Count.ToString();

        for (int i = 0; i < detail.Parts.Count; i++)
        {
            var part = detail.Parts[i];
            var tags = new List<string>();
            if (!string.IsNullOrWhiteSpace(part.DurationText)) tags.Add(part.DurationText);
            if (part.DanmakuCount > 0) tags.Add($"{part.DanmakuCount} 弹幕");

            Playlist.Add(new PlaylistEntry
            {
                IndexText = (i + 1).ToString(),
                Title = part.Title,
                SubText = string.Join(" · ", tags),
                Key = part.VideoId
            });
        }

        if (Playlist.Count == 0)
        {
            _playlistHint = "该视频没有分P信息";
        }

        if (string.IsNullOrEmpty(_currentVideoId)) _currentVideoId = detail.CurrentVideoId;
    }

    private async Task LoadBangumiInfoAsync(string bangumiId, string? title)
    {
        var detail = await _bangumi.GetBangumiDetailAsync(bangumiId);

        // SSR 页面偶尔返回不完整（bangumiData 有、剧集数组缺失），重试一次再降级
        if (detail is not null && detail.Episodes.Count == 0)
        {
            RuntimeLog.Write("bangumi", $"aaaa{bangumiId} 首次未解析到剧集，重试");
            await Task.Delay(500);
            detail = await _bangumi.GetBangumiDetailAsync(bangumiId) ?? detail;
        }

        if (detail is null)
        {
            TitleText.Text = string.IsNullOrWhiteSpace(title) ? $"番剧 aa{bangumiId}" : title!;
            MetaText.Text = "未能读取到番剧详情（接口可能调整）";
            _playlistHint = "没有剧集信息";
            _playlistTitle = "选集";
            PlaylistTab.Content = _playlistTitle;
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? detail.Title : title!;
        DescriptionText.Text = string.IsNullOrWhiteSpace(detail.Introduction) ? "暂无简介" : detail.Introduction;
        UpdateDescriptionToggle();
        AuthorText.Text = string.IsNullOrWhiteSpace(detail.UpdateStatus) ? detail.Title : $"{detail.Title} · {detail.UpdateStatus}";

        var meta = new List<string> { $"共 {detail.ItemCount} 话" };
        meta.Add($"{VideoDetailService.FormatCount(detail.PlayCount)} 播放");
        meta.Add($"{VideoDetailService.FormatCount(detail.StowCount)} 追番");
        if (detail.NeedPay) meta.Add("需付费");

        // 保存「当前第N集」之前的部分，切集时只需重拼这一句
        _bangumiMetaBase = meta;
        UpdateBangumiMeta(detail.EpisodeName);

        LikeText.Text = VideoDetailService.FormatCount(detail.LikeCount);
        FavoriteText.Text = VideoDetailService.FormatCount(detail.StowCount);

        if (!string.IsNullOrWhiteSpace(detail.Cover))
        {
            try { AuthorAvatar.ProfilePicture = new BitmapImage(new Uri(detail.Cover)); }
            catch { /* 封面加载失败不影响播放 */ }
        }

        _mkey = string.IsNullOrWhiteSpace(_mkey) ? detail.Mkey : _mkey;

        _playlistTitle = "选集";
        PlaylistTab.Content = _playlistTitle;
        ListCountText.Text = detail.Episodes.Count.ToString();

        for (int i = 0; i < detail.Episodes.Count; i++)
        {
            var episode = detail.Episodes[i];
            Playlist.Add(new PlaylistEntry
            {
                IndexText = (i + 1).ToString(),
                Title = episode.Name,
                SubText = episode.NeedPay ? "需付费" : string.Empty,
                Key = episode.VideoId
            });
        }

        if (Playlist.Count == 0)
        {
            _playlistHint = "没有取到剧集清单，已按当前集播放";
        }

        if (string.IsNullOrEmpty(_currentVideoId)) _currentVideoId = detail.CurrentVideoId;
    }

    // ───────────────────────────── 播放 ─────────────────────────────

    /// <summary>取流并播放当前集/分P</summary>
    private async Task PlayCurrentAsync()
    {
        Overlay.ShowStatus("正在获取播放地址…");

        // 换集/换源时控制条回到初始态（否则会短暂显示上一集的进度）
        Overlay.ResetProgress();
        Overlay.ShowControls(restartTimer: false);

        var response = await _play.GetPlayInfoAsync(
            _resourceId,
            _isBangumi ? 1 : 2,
            string.IsNullOrWhiteSpace(_currentVideoId) ? null : _currentVideoId,
            _mkey);

        var stream = _play.BuildPlayResult(response, AppSettings.Current.PreferHevc);

        if (!stream.HasStream)
        {
            Overlay.ShowStatus(stream.ErrorMessage ?? "拿不到播放地址", spinner: false);
            return;
        }

        _pendingResumeSeconds = stream.UserPlayedSeconds;
        _resumeAfterSourceSwitch = false;

        _suppressQualityChange = true;
        _qualityList.ItemsSource = stream.Qualities;
        _qualityList.SelectedIndex = ResolveQualityIndex(stream.Qualities);
        _suppressQualityChange = false;

        ApplyQuality(stream.Qualities[_qualityList.SelectedIndex]);

        // 弹幕按「分P / 集」的 videoId 取，和版本无关
        await LoadDanmakuAsync(_currentVideoId);

        // 切集后同步「当前 第N集」与控制层里的标题 / 集信息
        RefreshEpisodeLabel();
        UpdateOverlayInfo();
    }

    private void UpdateBangumiMeta(string? episodeName)
    {
        if (!_isBangumi) return;

        var parts = new List<string>(_bangumiMetaBase);
        if (!string.IsNullOrWhiteSpace(episodeName)) parts.Add($"当前 {episodeName}");
        MetaText.Text = string.Join(" · ", parts);
    }

    /// <summary>按当前 videoId 找到集名，刷新番剧元信息</summary>
    private void RefreshEpisodeLabel()
    {
        if (!_isBangumi) return;

        int index = IndexOfCurrent();
        UpdateBangumiMeta(index >= 0 ? Playlist[index].Title : null);
    }

    private static int ResolveQualityIndex(List<PlayQuality> qualities)
    {
        var preferred = AppSettings.Current.PreferredQuality;
        if (!string.Equals(preferred, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            int index = qualities.FindIndex(q =>
                string.Equals(q.Label, preferred, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return index;
        }

        return 0; // 已按分辨率降序，0 就是最高清
    }

    /// <summary>切到某档清晰度：先刷新线路列表，再按当前选中线路播放</summary>
    private void ApplyQuality(PlayQuality quality)
    {
        Overlay.SetQualityText(quality.Label);
        RefreshCdnBox(quality);
        PlaySelectedCdn();
    }

    /// <summary>
    /// 刷新「线路」下拉。列表来自该档清晰度的 CdnSources（主源 + backupUrl[]）。
    /// 默认按设置 PreferBackupCdn 决定选主源还是第一个备用源。
    /// </summary>
    private void RefreshCdnBox(PlayQuality quality)
    {
        _suppressCdnChange = true;

        _cdnList.ItemsSource = quality.CdnSources;
        // 只有一条线路时没什么可切，按钮直接藏起来
        Overlay.SetLineVisible(quality.CdnSources.Count > 1);

        if (quality.CdnSources.Count == 0)
        {
            _cdnList.SelectedIndex = -1;
            Overlay.SetLineText("线路");
            _suppressCdnChange = false;
            return;
        }

        int index = 0;
        if (AppSettings.Current.PreferBackupCdn && quality.CdnSources.Count > 1) index = 1;
        _cdnList.SelectedIndex = index;
        Overlay.SetLineText(quality.CdnSources[index].Name);

        _suppressCdnChange = false;
    }

    /// <summary>用当前选中的「清晰度 + 线路」组合真正起播</summary>
    private void PlaySelectedCdn()
    {
        if (_qualityList.SelectedItem is not PlayQuality quality) return;

        var cdn = _cdnList.SelectedItem as CdnSource;
        var url = cdn?.Url;

        if (string.IsNullOrWhiteSpace(url))
        {
            url = AppSettings.Current.PreferBackupCdn && !string.IsNullOrWhiteSpace(quality.BackupUrl)
                ? quality.BackupUrl!
                : quality.Url;
        }

        // AcFun 的 HLS 分片是相对路径、签名却挂在清单的 query 上，
        // 播放器解析相对路径会把 query 丢掉 → 分片全部 403。
        // 所以先过一遍本地代理，让它把分片重写成带签名的绝对地址。
        _currentUrl = LocalMediaProxy.Current.RegisterHls(url!);
        _ffmpegFallbackTried = false;

        RuntimeLog.Write("play", $"清晰度={quality.Label} 线路={cdn?.Name ?? "默认"} 走代理 {_currentUrl} ← {url}");

        Overlay.MediaElement.Source = MediaSource.CreateFromUri(new Uri(_currentUrl));
        Overlay.MediaElement.AutoPlay = AppSettings.Current.AutoPlay;

        if (AppSettings.Current.AutoPlay) Overlay.MediaElement.MediaPlayer?.Play();
    }

    /// <summary>
    /// 播放失败：先看是不是系统 HLS 解码器不认，退一步用 FFmpegInteropX 再解一次。
    /// </summary>
    private async void Overlay_MediaFailed(object? sender, string message)
    {
        if (_ffmpegFallbackTried || string.IsNullOrWhiteSpace(_currentUrl))
        {
            Overlay.ShowStatus($"播放失败：{message}", spinner: false);
            return;
        }

        _ffmpegFallbackTried = true;
        Overlay.ShowStatus("系统解码器不支持该流，正在切换 FFmpeg 解码…");

        try
        {
            Overlay.MediaElement.Source = await FFmpegPlayback.CreateAsync(_currentUrl, AppSettings.Current.LiveHardwareDecode);
            Overlay.MediaElement.AutoPlay = true;
            Overlay.MediaElement.MediaPlayer?.Play();

            RuntimeLog.Write("play", $"FFmpeg 兜底成功，url={_currentUrl}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("play", $"FFmpeg 兜底失败：{ex}");
            Overlay.ShowStatus($"FFmpeg 解码也失败了：{ex.Message}\n（系统错误：{message}）", spinner: false);
        }
    }

    private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressQualityChange) return;
        if (_qualityList.SelectedItem is PlayQuality quality)
        {
            // 换清晰度不该把播放位置丢掉：记下当前位置，MediaOpened 里按续播逻辑跳回去
            _pendingResumeSeconds = (long)(Overlay.MediaElement.MediaPlayer?.PlaybackSession?.Position.TotalSeconds ?? 0);
            _resumeAfterSourceSwitch = _pendingResumeSeconds > 5;
            Overlay.SetQualityText(quality.Label);
            Overlay.ShowStatus($"正在切换到 {quality.Label}…");
            ApplyQuality(quality);
        }
    }

    private void CdnBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCdnChange) return;
        if (_cdnList.SelectedItem is CdnSource cdn)
        {
            _pendingResumeSeconds = (long)(Overlay.MediaElement.MediaPlayer?.PlaybackSession?.Position.TotalSeconds ?? 0);
            _resumeAfterSourceSwitch = _pendingResumeSeconds > 5;
            Overlay.SetLineText(cdn.Name);
            Overlay.ShowStatus($"正在切换到 {cdn.Name}…");
            PlaySelectedCdn();
        }
    }

    // ───────────────────────────── 上下集 ─────────────────────────────

    private int IndexOfCurrent()
    {
        for (int i = 0; i < Playlist.Count; i++)
        {
            if (string.Equals(Playlist[i].Key, _currentVideoId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private void UpdateEpisodeButtons()
    {
        int index = IndexOfCurrent();
        Overlay.SetEpisodeNavigation(index > 0, index >= 0 && index < Playlist.Count - 1);
    }

    private async Task PlayRelativeAsync(int delta)
    {
        int index = IndexOfCurrent();
        if (index < 0) return;

        int target = index + delta;
        if (target < 0 || target >= Playlist.Count) return;

        _currentVideoId = Playlist[target].Key;
        EpisodeList.SelectedIndex = target;

        await PlayCurrentAsync();
        UpdateEpisodeButtons();
    }

    // ───────────────────────────── 全屏 / 返回 ─────────────────────────────

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Overlay.IsFullscreen) return;

        Overlay.ExitFullscreen();
        args.Handled = true;
    }

    /// <summary>
    /// 全屏四态里的 Fullscreen：内容行高度归零、内边距清零、兜底底色显示。
    /// 退出时按当前窗口宽度重算，不还原写死的旧数值。
    /// </summary>
    private void Overlay_FullscreenChanged(object? sender, bool fullscreen)
    {
        if (fullscreen)
        {
            LayoutGrid.Padding = new Thickness(0);
            LayoutGrid.RowSpacing = 0;
            LayoutGrid.ColumnSpacing = 0;
            // 右列整条让出去，播放器才能独占整个窗口
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

    // ───────────────────────────── 信息区交互 ─────────────────────────────

    /// <summary>简介超过两行才给「展开简介」按钮，展开高度变化只影响左列内部</summary>
    private void UpdateDescriptionToggle()
    {
        _descriptionExpanded = false;
        DescriptionText.MaxLines = 2;
        DescriptionToggle.Content = "展开简介";

        bool longText = (DescriptionText.Text?.Length ?? 0) > 90;
        DescriptionToggle.Visibility = longText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DescriptionToggle_Click(object sender, RoutedEventArgs e)
    {
        _descriptionExpanded = !_descriptionExpanded;
        DescriptionText.MaxLines = _descriptionExpanded ? 0 : 2;
        DescriptionToggle.Content = _descriptionExpanded ? "收起简介" : "展开简介";
    }

    /// <summary>
    /// 点赞 / 收藏：AcFun 的写接口需要登录态，当前版本只读。
    /// 做成真正的按钮（可聚焦、有悬停反馈），点击时明确说明而不是装死。
    /// </summary>
    private void LikeButton_Click(object sender, RoutedEventArgs e)
        => ShowUnavailableFlyout((FrameworkElement)sender, "点赞", "点赞需要登录态的写接口，当前版本尚未接入。");

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        => ShowUnavailableFlyout((FrameworkElement)sender, "收藏", "收藏需要登录态的写接口，当前版本尚未接入。");

    private static void ShowUnavailableFlyout(FrameworkElement target, string title, string message)
    {
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
            Content = new StackPanel
            {
                Spacing = 4,
                MaxWidth = 260,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.75
                    }
                }
            }
        };

        flyout.ShowAt(target);
    }

    private void GoBack()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(HomePage));
    }

    /// <summary>播放器内的标题 / 集信息（顶栏 + 控制层左信息位）</summary>
    private void UpdateOverlayInfo()
    {
        string title = TitleText.Text;

        int index = IndexOfCurrent();
        string episode = index >= 0 ? Playlist[index].Title : string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(AuthorText.Text)) parts.Add(AuthorText.Text);
        if (!string.IsNullOrWhiteSpace(episode)) parts.Add(episode);

        Overlay.SetTitle(
            title,
            string.Join(" · ", parts),
            index >= 0 ? $"第 {index + 1} / 共 {Playlist.Count} 集" : string.Empty);
    }

    // ───────────────────────────── 弹幕 ─────────────────────────────

    private async Task LoadDanmakuAsync(string? videoId)
    {
        _danmakuTimer.Stop();
        _danmakus.Clear();
        _danmakuCursor = 0;
        _lastPosition = -1;
        Overlay.Danmaku.ClearAll();

        if (string.IsNullOrWhiteSpace(videoId))
        {
            RuntimeLog.Write("danmaku", "videoId 为空，跳过取弹幕");
            return;
        }

        var list = await _danmaku.GetClientDanmakusAsync(videoId);
        _danmakus.AddRange(list);

        RuntimeLog.Write(
            "danmaku",
            $"videoId={videoId} 取到 {_danmakus.Count} 条，开关={Overlay.DanmakuEnabled}");

        if (Overlay.DanmakuEnabled && _danmakus.Count > 0)
        {
            _danmakuTimer.Start();
        }
    }

    private void DanmakuTimer_Tick(object? sender, object e)
    {
        var session = Overlay.MediaElement.MediaPlayer?.PlaybackSession;
        if (session is null || _danmakus.Count == 0) return;

        double position = session.Position.TotalSeconds;

        // 拖动进度条回退时重置游标
        if (_lastPosition >= 0 && position < _lastPosition - 1.5)
        {
            _danmakuCursor = 0;
            Overlay.Danmaku.ClearAll();
        }

        _lastPosition = position;

        while (_danmakuCursor < _danmakus.Count && _danmakus[_danmakuCursor].TimeSeconds <= position)
        {
            var item = _danmakus[_danmakuCursor];

            // 刚跳转过来时不要补放一大堆积压弹幕
            if (position - item.TimeSeconds <= 1.5)
            {
                Overlay.Danmaku.AddDanmaku(item.Text, item.FontSize, item.Color, item.Mode);
            }

            _danmakuCursor++;
        }
    }

    private void Overlay_DanmakuToggled(object? sender, bool enabled)
    {
        if (enabled && _danmakus.Count > 0)
        {
            _danmakuTimer.Start();
        }
        else
        {
            _danmakuTimer.Stop();
            Overlay.Danmaku.ClearAll();
        }
    }

    /// <summary>拖完进度条 / ±10 秒后把弹幕游标重新对齐，避免向前拖时漏放一屏弹幕</summary>
    private void ResyncDanmakuCursor(double seconds)
    {
        _danmakuCursor = _danmakus.FindIndex(d => d.TimeSeconds >= seconds);
        if (_danmakuCursor < 0) _danmakuCursor = _danmakus.Count;
        _lastPosition = -1;
        Overlay.Danmaku.ClearAll();
    }

    // ───────────────────────────── 右栏切换（选集 / 相关） ─────────────────────────────

    private void SideTab_Click(object sender, RoutedEventArgs e)
    {
        SelectSideTab(ReferenceEquals(sender, RelatedTab) ? 1 : 0);
    }

    private void SelectSideTab(int index)
    {
        PlaylistTab.IsChecked = index == 0;
        RelatedTab.IsChecked = index == 1;

        EpisodeList.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        RelatedList.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;

        ListCountText.Text = index switch
        {
            1 => RelatedVideos.Count > 0 ? RelatedVideos.Count.ToString() : string.Empty,
            _ => Playlist.Count.ToString()
        };

        UpdateSideHint(index);
    }

    /// <summary>侧栏空态统一走 StatePanel（叠在列表上，不占布局）</summary>
    private void UpdateSideHint(int index)
    {
        if (index == 0)
        {
            if (Playlist.Count > 0)
            {
                SideState.Hide();
                return;
            }

            SideState.ShowEmpty(
                _isBangumi ? "没有取到剧集清单" : "该视频没有分P信息",
                _playlistHint,
                glyph: "\uE7BA");
        }
        else
        {
            if (RelatedVideos.Count > 0)
            {
                SideState.Hide();
                return;
            }

            SideState.ShowEmpty(
                _isBangumi ? "番剧暂无相关推荐" : "暂无相关视频",
                _isBangumi ? "番剧接口不返回同频道内容流。" : null,
                glyph: "\uE71E");
        }
    }

    // ───────────────────────────── 评论 ─────────────────────────────

    private async Task LoadCommentsAsync(bool append)
    {
        // 番剧的评论 sourceType 与视频不同，实测传 2 拿不到就退回 3 再试
        int sourceType = _isBangumi ? 2 : 3;

        if (!append)
        {
            CommentState.ShowLoading("正在加载评论…");
        }

        CommentLoadingRing.IsActive = true;
        CommentLoadingRing.Visibility = Visibility.Visible;

        int page = append ? _commentPage + 1 : 1;
        var result = await _comment.GetCommentsAsync(_resourceId, sourceType, page);

        if (result.Items.Count == 0 && _isBangumi && !append)
        {
            var retry = await _comment.GetCommentsAsync(_resourceId, 3, 1);
            if (retry.Items.Count > 0) result = retry;
        }

        if (!append) Comments.Clear();

        foreach (var item in result.Items) Comments.Add(item);

        _commentPage = page;
        _commentsLoaded = true;
        _commentTotalPage = result.TotalPage;
        _commentTotalCount = result.TotalCount;

        CommentLoadingRing.IsActive = false;
        CommentLoadingRing.Visibility = Visibility.Collapsed;

        if (result.ErrorMessage is not null && Comments.Count == 0)
        {
            CommentState.ShowError("评论加载失败", result.ErrorMessage, "重试");
        }
        else
        {
            UpdateCommentState();
        }

        UpdateLoadMoreButton();
    }

    /// <summary>评论区三态：加载中 / 空 / 有内容</summary>
    private void UpdateCommentState()
    {
        CommentCountText.Text = _commentTotalCount > 0
            ? $"共 {VideoDetailService.FormatCount(_commentTotalCount)} 条"
            : (Comments.Count > 0 ? $"{Comments.Count} 条" : string.Empty);

        if (!_commentsLoaded)
        {
            CommentState.ShowLoading("正在加载评论…");
            return;
        }

        if (Comments.Count == 0)
        {
            CommentState.ShowEmpty("暂无评论", "还没有人发言", glyph: "\uE90A");
            return;
        }

        CommentState.Hide();
    }

    private async void CommentState_ActionClick(object sender, EventArgs e)
    {
        await LoadCommentsAsync(append: false);
    }

    private void UpdateLoadMoreButton()
    {
        LoadMoreCommentButton.Visibility =
            _commentsLoaded && Comments.Count > 0 && _commentPage < _commentTotalPage
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void LoadMoreCommentButton_Click(object sender, RoutedEventArgs e)
    {
        LoadMoreCommentButton.Visibility = Visibility.Collapsed;
        await LoadCommentsAsync(append: true);
    }

    /// <summary>展开 / 收起某条主楼的楼中楼</summary>
    private void ReplyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is CommentItem item)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    // ───────────────────────────── 相关视频 ─────────────────────────────

    private async Task LoadRelatedAsync()
    {
        if (_isBangumi || string.IsNullOrWhiteSpace(_channelId))
        {
            if (RelatedTab.IsChecked == true) SelectSideTab(1);
            return;
        }

        var list = await _related.GetRelatedAsync(_channelId, excludeDougaId: _resourceId, max: 20);

        RelatedVideos.Clear();
        foreach (var item in list) RelatedVideos.Add(item);

        if (RelatedTab.IsChecked == true) SelectSideTab(1);
    }

    private async void RelatedList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RelatedVideoItem item) return;
        if (string.IsNullOrWhiteSpace(item.DougaId)) return;

        // 同页内切换，不新建页面实例
        await LoadAsync(item.DougaId, 2, null, null, null);
        BodyScroll.ChangeView(null, 0, null);
    }

    private async void EpisodeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaylistEntry entry) return;
        if (string.Equals(entry.Key, _currentVideoId, StringComparison.Ordinal)) return;

        _currentVideoId = entry.Key;
        await PlayCurrentAsync();
        UpdateEpisodeButtons();
    }

    /// <summary>续播：服务端下发过 userPlayedSeconds 时跳到上次进度（本地也能用）</summary>
    private void Overlay_MediaOpened(object? sender, EventArgs e)
    {
        _ffmpegFallbackTried = false;

        // 换清晰度 / 线路属于「同一集换源」，不管续播开关开没开都要回到原位
        bool shouldResume = AppSettings.Current.ResumePlayback || _resumeAfterSourceSwitch;
        _resumeAfterSourceSwitch = false;

        if (!shouldResume || _pendingResumeSeconds <= 5) return;

        var session = Overlay.MediaElement.MediaPlayer?.PlaybackSession;
        if (session is null) return;

        // 只在进度明显早于续播点时跳转，避免片尾秒退
        if (session.NaturalDuration.TotalSeconds > _pendingResumeSeconds + 10)
        {
            Overlay.SeekTo(_pendingResumeSeconds);
        }
    }
}
