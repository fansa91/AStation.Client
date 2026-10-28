using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
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
///   · 相关视频 POST /rest/pc-direct/channel/resource/query/match（同频道内容流）
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

    /// <summary>分P/选集区的空状态文案（列表为空时显示）</summary>
    private string? _playlistHint;

    /// <summary>分P/选集分页的标题（切到评论/相关后切回来要还原）</summary>
    private string _playlistTitle = "分P";

    private bool _isFullscreen;
    private double _savedPlayerHeight;

    /// <summary>控制条自动隐藏计时器（播放中且鼠标离开画面 2.5 秒后收起）</summary>
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    /// <summary>用户正按住进度条拖动（此时不接受播放器的进度回写）</summary>
    private bool _isSeeking;

    /// <summary>程序内部回写 Slider 时置位，避免触发 ValueChanged 里的 seek</summary>
    private bool _suppressProgress;

    /// <summary>鼠标是否停在播放器区域内（窗口模式下在里面时控制条常显）</summary>
    private bool _pointerInsidePlayer;

    /// <summary>鼠标是否正停在控制条上（此时不启动收起计时，避免操作时被藏掉）</summary>
    private bool _hoveringControlBar;

    /// <summary>控制层当前是否展开（收起后底部改走细进度条）</summary>
    private bool _controlsVisible = true;

    /// <summary>倍速气泡的失效令牌，连续调倍速时只有最后一次的延时能把它收起来</summary>
    private int _speedBubbleToken;

    /// <summary>倍速面板是否已真正打开（Flyout 内容延迟加载，打开前的 Value 回写要忽略）</summary>
    private bool _speedPanelReady;

    /// <summary>当前倍速（换集/换源后 PlaybackRate 会被重置，需要重新贴回去）</summary>
    private double _playbackRate = 1.0;

    /// <summary>代码回写倍速下拉框时置位，避免再触发一次变速</summary>
    private bool _suppressSpeedChange;

    /// <summary>进入全屏前的页面背景（退出时要还原）</summary>
    private Brush? _savedPageBackground;

    /// <summary>番剧元信息的固定部分（不含「当前第N集」）</summary>
    private List<string> _bangumiMetaBase = new();

    public ObservableCollection<PlaylistEntry> Playlist { get; } = new();
    public ObservableCollection<CommentItem> Comments { get; } = new();
    public ObservableCollection<RelatedVideoItem> RelatedVideos { get; } = new();

    public VideoPage()
    {
        InitializeComponent();
        _danmakuTimer.Tick += DanmakuTimer_Tick;

        // MediaPlayerElement 本身没有 MediaOpened / MediaFailed，要挂到内部 MediaPlayer 上
        Player.MediaPlayer.MediaOpened += Player_MediaOpened;
        Player.MediaPlayer.MediaFailed += Player_MediaFailed;

        // 进度条 / 播放状态：控制条自己画进度条，所以直接挂 PlaybackSession
        var session = Player.MediaPlayer.PlaybackSession;
        if (session is not null)
        {
            session.PositionChanged += PlaybackSession_PositionChanged;
            session.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        }

        _controlsTimer.Tick += (_, _) =>
        {
            _controlsTimer.Stop();
            HideControlsIfPlaying();
        };

        // MediaPlayerElement 会把指针事件标记为已处理，普通订阅收不到；
        // 必须 handledEventsToo: true 才能在全屏（视频独占渲染）时也唤出控制条
        PlayerHost.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(PlayerHost_PointerMoved),
            handledEventsToo: true);
        PlayerHost.AddHandler(
            UIElement.PointerEnteredEvent,
            new PointerEventHandler(PlayerHost_PointerMoved),
            handledEventsToo: true);
        PlayerHost.PointerExited += PlayerHost_PointerExited;

        EpisodeList.ItemsSource = Playlist;
        CommentList.ItemsSource = Comments;
        RelatedList.ItemsSource = RelatedVideos;

        SizeChanged += VideoPage_SizeChanged;
        ApplyDanmakuSettings();

        // 控制层的初始态：倍速文字 / 静音图标 / 窄宽度断点
        UpdateSpeedUi();
        UpdateVolumeIcon();

        // 记住页面背景，全屏时会临时改成纯黑（防止漏出浅色边），退出时还原
        _savedPageBackground = PageRoot.Background;
    }

    private void ApplyDanmakuSettings()
    {
        var settings = AppSettings.Current;
        DanmakuControl.SpeedSeconds = settings.DanmakuSpeedSeconds;
        DanmakuControl.AreaRatio = settings.DanmakuArea;
        DanmakuControl.DanmakuOpacity = settings.DanmakuOpacity;
        DanmakuToggle.IsChecked = settings.DanmakuEnabled;
        DanmakuLayer.Visibility = settings.DanmakuEnabled ? Visibility.Visible : Visibility.Collapsed;
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
                ShowStatus("没有可播放的内容");
                LoadingRing.IsActive = false;
                break;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _danmakuTimer.Stop();
        DanmakuControl.ClearAll();
        Player.MediaPlayer?.Pause();
        Player.Source = null;

        // 离开页面时务必还原窗口呈现方式，否则会卡在全屏
        if (_isFullscreen) ExitFullscreen();
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

        Playlist.Clear();
        _playlistHint = null;
        _playlistTitle = _isBangumi ? "选集" : "分P";
        LoadingRing.IsActive = true;
        ShowStatus("正在加载播放数据…");
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
            ShowStatus($"读取页面信息失败：{ex.Message}");
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
            ListTitle.Text = _playlistTitle;
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? detail.Title : title!;
        DescriptionText.Text = string.IsNullOrWhiteSpace(detail.Description) ? "暂无简介" : detail.Description;
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
        ListTitle.Text = _playlistTitle;
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
            ListTitle.Text = _playlistTitle;
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? detail.Title : title!;
        DescriptionText.Text = string.IsNullOrWhiteSpace(detail.Introduction) ? "暂无简介" : detail.Introduction;
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
        ListTitle.Text = _playlistTitle;
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
        ShowStatus("正在获取播放地址…");

        // 换集/换源时控制条回到初始态（否则会短暂显示上一集的进度）
        _suppressProgress = true;
        ProgressSlider.Maximum = 1;
        ProgressSlider.Value = 0;
        _suppressProgress = false;
        PositionText.Text = "00:00";
        DurationText.Text = "00:00";
        PlayPauseIcon.Glyph = "\uE768";
        ShowControls(restartTimer: false);

        var response = await _play.GetPlayInfoAsync(
            _resourceId,
            _isBangumi ? 1 : 2,
            string.IsNullOrWhiteSpace(_currentVideoId) ? null : _currentVideoId,
            _mkey);

        var stream = _play.BuildPlayResult(response, AppSettings.Current.PreferHevc);

        if (!stream.HasStream)
        {
            LoadingRing.IsActive = false;
            ShowStatus(stream.ErrorMessage ?? "拿不到播放地址");
            return;
        }

        _pendingResumeSeconds = stream.UserPlayedSeconds;

        _suppressQualityChange = true;
        QualityBox.ItemsSource = stream.Qualities;
        QualityBox.SelectedIndex = ResolveQualityIndex(stream.Qualities);
        _suppressQualityChange = false;

        ApplyQuality(stream.Qualities[QualityBox.SelectedIndex]);

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
        QualityText.Text = quality.Label;
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

        CdnBox.ItemsSource = quality.CdnSources;
        // 只有一条线路时没什么可切，按钮直接藏起来
        CdnButton.Visibility = quality.CdnSources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (quality.CdnSources.Count == 0)
        {
            CdnBox.SelectedIndex = -1;
            CdnText.Text = "线路";
            _suppressCdnChange = false;
            return;
        }

        int index = 0;
        if (AppSettings.Current.PreferBackupCdn && quality.CdnSources.Count > 1) index = 1;
        CdnBox.SelectedIndex = index;
        CdnText.Text = quality.CdnSources[index].Name;

        _suppressCdnChange = false;
    }

    /// <summary>用当前选中的「清晰度 + 线路」组合真正起播</summary>
    private void PlaySelectedCdn()
    {
        if (QualityBox.SelectedItem is not PlayQuality quality) return;

        var cdn = CdnBox.SelectedItem as CdnSource;
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

        Player.Source = MediaSource.CreateFromUri(new Uri(_currentUrl));
        Player.AutoPlay = AppSettings.Current.AutoPlay;

        if (AppSettings.Current.AutoPlay) Player.MediaPlayer?.Play();
    }

    /// <summary>
    /// 播放失败：先看是不是系统 HLS 解码器不认，退一步用 FFmpegInteropX 再解一次。
    /// </summary>
    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? args.Error.ToString()
            : args.ErrorMessage;

        RuntimeLog.Write("play", $"系统解码失败：{args.Error} / {message}");

        DispatcherQueue.TryEnqueue(async () => await TryFfmpegFallbackAsync(message));
    }

    private async Task TryFfmpegFallbackAsync(string systemError)
    {
        if (_ffmpegFallbackTried || string.IsNullOrWhiteSpace(_currentUrl))
        {
            LoadingRing.IsActive = false;
            ShowStatus($"播放失败：{systemError}");
            return;
        }

        _ffmpegFallbackTried = true;
        ShowStatus("系统解码器不支持该流，正在切换 FFmpeg 解码…");

        try
        {
            Player.Source = await FFmpegPlayback.CreateAsync(_currentUrl, AppSettings.Current.LiveHardwareDecode);
            Player.AutoPlay = true;
            Player.MediaPlayer?.Play();

            RuntimeLog.Write("play", $"FFmpeg 兜底成功，url={_currentUrl}");
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            RuntimeLog.Write("play", $"FFmpeg 兜底失败：{ex}");
            ShowStatus($"FFmpeg 解码也失败了：{ex.Message}\n（系统错误：{systemError}）");
            return;
        }
    }

    private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressQualityChange) return;
        if (QualityBox.SelectedItem is PlayQuality quality)
        {
            QualityText.Text = quality.Label;
            ApplyQuality(quality);
        }
    }

    private void CdnBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCdnChange) return;
        if (CdnBox.SelectedItem is CdnSource cdn)
        {
            CdnText.Text = cdn.Name;
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
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index >= 0 && index < Playlist.Count - 1;
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

    private async void PrevButton_Click(object sender, RoutedEventArgs e) => await PlayRelativeAsync(-1);

    private async void NextButton_Click(object sender, RoutedEventArgs e) => await PlayRelativeAsync(1);

    // ───────────────────────────── 全屏 ─────────────────────────────

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isFullscreen) return;

        ExitFullscreen();
        args.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        try
        {
            EnterFullscreenCore();
        }
        catch (Exception ex)
        {
            // 全屏要动 AppWindow 与 NavigationView，任何一步失败都不该把整个播放页带崩
            RuntimeLog.Write("fullscreen", $"进入全屏异常：{ex}");
            _isFullscreen = false;
        }
    }

    private void EnterFullscreenCore()
    {
        _isFullscreen = true;
        _savedPlayerHeight = PlayerHost.Height;

        // 用 Win32 直接做无边框全屏：AppWindow 的 FullScreen 呈现器在这台系统上
        // 会给客户区留 1px 边框，屏幕顶部就多一条白线
        var window = App.MainWindowInstance;
        RuntimeLog.Write("fullscreen", $"进入全屏：MainWindowInstance={(window is null ? "null" : "ok")}");

        // 顺序要紧：先收起外壳（改标题栏行高会让 WinUI 重新套一遍窗口样式），
        // 再把窗口样式改成 WS_POPUP 并铺满显示器，否则刚去掉的边框又会被加回来
        window?.SetChromeVisible(false);
        window?.EnterBorderlessFullscreen();

        LayoutGrid.Padding = new Thickness(0);
        // 第二列宽度归 0 后 ColumnSpacing 仍然会留 18px —— 全屏时右侧就是一条白边，必须一起清零
        LayoutGrid.ColumnSpacing = 0;
        SideColumn.Width = new GridLength(0);
        SidePanel.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        PlayerHost.CornerRadius = new CornerRadius(0);
        RootScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        RootScroll.VerticalScrollMode = ScrollMode.Disabled;

        // 兜底：万一路由区还有几个像素的残余，看到的也应该是黑色留边而不是浅色白边
        PageRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);

        // 顶栏只在全屏显示：窗口模式下播放器上方就是页面标题，再来一条会重复
        TopBar.Visibility = Visibility.Visible;
        FullscreenIcon.Glyph = "\uE73F";

        // 全屏后可用区域是整个屏幕，播放器铺满
        PlayerHost.Height = XamlRoot?.Size.Height > 0 ? XamlRoot.Size.Height : _savedPlayerHeight;
        ShowControls();

        // 布局落定后记录一次真实几何，白边问题排查全靠这行
        DispatcherQueue.TryEnqueue(LogFullscreenGeometry);
        _ = LogFullscreenGeometryLaterAsync();
        StartControlsCountdownDelayed();
    }

    /// <summary>全屏几何：播放器相对窗口原点的位置 + 尺寸，用来验证有没有残留边距</summary>
    private void LogFullscreenGeometry()
    {
        try
        {
            var root = XamlRoot?.Size ?? new Windows.Foundation.Size(0, 0);
            var origin = PlayerHost.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            RuntimeLog.Write(
                "fullscreen",
                $"全屏几何：player=({origin.X:F0},{origin.Y:F0}) {PlayerHost.ActualWidth:F0}x{PlayerHost.ActualHeight:F0} " +
                $"xamlroot={root.Width:F0}x{root.Height:F0} padding={LayoutGrid.Padding.Left:F0} " +
                $"spacing={LayoutGrid.ColumnSpacing:F0}");
            RuntimeLog.Write("fullscreen", "外壳：" + (App.MainWindowInstance?.DescribeChrome() ?? "null"));
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("fullscreen", $"读取几何失败：{ex.Message}");
        }
    }

    private async Task LogFullscreenGeometryLaterAsync()
    {
        await Task.Delay(900);
        DispatcherQueue.TryEnqueue(LogFullscreenGeometry);
    }

    /// <summary>进入全屏后强制开始计时（此时指针正好停在刚点过的按钮上）</summary>
    private async void StartControlsCountdownDelayed()
    {
        await Task.Delay(700);
        if (!_isFullscreen) return;

        // 进全屏时窗口尺寸变了，控制条上的 PointerExited 有可能收不到，
        // hover 状态会卡在 true，计时器就永远不启动 —— 这里先复位一次
        _hoveringControlBar = false;
        StartControlsCountdown();
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;

        App.MainWindowInstance?.ExitBorderlessFullscreen();
        App.MainWindowInstance?.SetChromeVisible(true);

        LayoutGrid.Padding = new Thickness(20, 4, 20, 24);
        LayoutGrid.ColumnSpacing = 18;
        SideColumn.Width = new GridLength(340);
        SidePanel.Visibility = Visibility.Visible;
        InfoPanel.Visibility = Visibility.Visible;
        PlayerHost.CornerRadius = new CornerRadius(10);
        RootScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        RootScroll.VerticalScrollMode = ScrollMode.Enabled;

        PageRoot.Background = _savedPageBackground ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        _controlsTimer.Stop();
        TopBar.Visibility = Visibility.Collapsed;
        FullscreenIcon.Glyph = "\uE740";
        ShowControls(restartTimer: false);

        // 退出全屏后窗口尺寸变化是异步的，下一帧再按 16:9 重算
        DispatcherQueue.TryEnqueue(UpdatePlayerHeight);
    }

    private void VideoPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreen && e.NewSize.Height > 0)
        {
            PlayerHost.Height = e.NewSize.Height;
        }
    }

    private void UpdatePlayerHeight()
    {
        if (_isFullscreen || PlayerHost.ActualWidth <= 0) return;

        // 视频区就是 16:9，控制层叠在画面底部（不额外占一行高度）
        double target = Math.Round(PlayerHost.ActualWidth * 9 / 16);
        if (Math.Abs(PlayerHost.Height - target) >= 1) PlayerHost.Height = target;
    }

    /// <summary>
    /// 按播放器宽度收窄控制层（对应 Bili.Copilot 的 Medium / Narrow 断点）：
    /// 窄了先撤掉左侧信息位，再撤掉音量滑杆，只留图标按钮。
    /// </summary>
    private void UpdateControlBreakpoints()
    {
        double width = PlayerHost.ActualWidth;
        if (width <= 0) return;

        // 左信息位要占掉一整列，窄了先把文字撤掉（否则会顶到中间的播放组下面）
        PlayerInfoSlot.Visibility = width >= 1000 ? Visibility.Visible : Visibility.Collapsed;

        // 音量滑杆 80px 是右列里最占位的，再窄就只留图标按钮
        VolumeSlider.Visibility = width >= 880 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>播放器内的标题 / 集信息（控制层左信息位 + 全屏顶栏）</summary>
    private void UpdateOverlayInfo()
    {
        string title = TitleText.Text;
        OverlayTitle.Text = title;
        PlayerTitleText.Text = title;

        int index = IndexOfCurrent();
        string episode = index >= 0 ? Playlist[index].Title : string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(AuthorText.Text)) parts.Add(AuthorText.Text);
        if (!string.IsNullOrWhiteSpace(episode)) parts.Add(episode);
        PlayerSubText.Text = string.Join(" · ", parts);

        OverlayEpisodeText.Text = index >= 0
            ? $"第 {index + 1} / 共 {Playlist.Count} 集"
            : string.Empty;
    }

    // ───────────────────────── 控制层（顶栏 + 控制条 + 气泡） ─────────────────────────

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        var mp = Player.MediaPlayer;
        if (mp?.PlaybackSession is null) return;

        if (mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) mp.Pause();
        else mp.Play();

        ShowControls();
    }

    /// <summary>控制条中间的前后跳过按钮（±10 秒）</summary>
    private void SkipBackward_Click(object sender, RoutedEventArgs e) => SeekRelative(-10);

    private void SkipForward_Click(object sender, RoutedEventArgs e) => SeekRelative(10);

    private void SeekRelative(double seconds)
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;

        var duration = session.NaturalDuration;
        var target = session.Position + TimeSpan.FromSeconds(seconds);

        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && target > duration) target = duration;

        session.Position = target;
        ResyncDanmakuCursor(target.TotalSeconds);

        _suppressProgress = true;
        ProgressSlider.Value = target.TotalSeconds;
        _suppressProgress = false;

        PositionText.Text = FormatTime(target);
        DurationText.Text = FormatTime(duration);
        ShowControls();
    }

    // ───────────────────────────── 音量 ─────────────────────────────

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var mp = Player.MediaPlayer;
        if (mp is null) return;

        mp.Volume = Math.Clamp(e.NewValue, 0, 1);

        // 主动拖音量就当用户想听声音了
        if (mp.Volume > 0.001 && mp.IsMuted) mp.IsMuted = false;

        UpdateVolumeIcon();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        var mp = Player.MediaPlayer;
        if (mp is null) return;

        mp.IsMuted = !mp.IsMuted;
        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon()
    {
        var mp = Player.MediaPlayer;
        bool muted = mp is null || mp.IsMuted || mp.Volume <= 0.001;
        VolumeIcon.Glyph = muted ? "\uE74F" : "\uE767";
    }

    // ───────────────────────────── 倍速 ─────────────────────────────

    /// <summary>倍速面板里的快捷档位（0.5 / 0.75 / 1.0 / 1.25 / 1.5 / 2.0）</summary>
    private void SpeedQuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        if (!double.TryParse(button.Tag?.ToString(), out double rate) || rate <= 0) return;

        SetPlaybackRate(rate);
    }

    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSpeedChange) return;

        // Flyout 里的控件是延迟加载的，首次布局会用 Minimum 回写一次 Value，
        // 面板没真正打开之前不接受任何变更
        if (!_speedPanelReady) return;

        SetPlaybackRate(Math.Round(e.NewValue, 2));
    }

    private void SpeedFlyout_Opened(object? sender, object e)
    {
        _suppressSpeedChange = true;
        SpeedSlider.Value = Math.Clamp(_playbackRate, SpeedSlider.Minimum, SpeedSlider.Maximum);
        _suppressSpeedChange = false;

        _speedPanelReady = true;
    }

    private void SpeedFlyout_Closed(object? sender, object e) => _speedPanelReady = false;

    private void SetPlaybackRate(double rate)
    {
        _playbackRate = rate;
        ApplyPlaybackRate();
        UpdateSpeedUi();
        ShowSpeedBubble(rate);
    }

    /// <summary>把当前倍速同步回 UI（按钮文字 / 滑杆 / 快捷档位选中态）</summary>
    private void UpdateSpeedUi()
    {
        _suppressSpeedChange = true;

        SpeedText.Text = FormatRate(_playbackRate);
        SpeedSlider.Value = Math.Clamp(_playbackRate, SpeedSlider.Minimum, SpeedSlider.Maximum);

        foreach (var child in SpeedQuickPanel.Children.OfType<ToggleButton>())
        {
            child.IsChecked = double.TryParse(child.Tag?.ToString(), out double tag)
                && Math.Abs(tag - _playbackRate) < 0.001;
        }

        _suppressSpeedChange = false;
    }

    private static string FormatRate(double rate) => $"{rate:0.0#}x";

    /// <summary>
    /// 把当前倍速贴到播放会话上。换集 / 换源后 PlaybackRate 会被重置，
    /// 所以 MediaOpened 里也要再调一次。部分源（如 FFmpeg 软解、直播）不支持变速，
    /// 这里只记日志并回滚下拉框，不让它抛到 UI 线程。
    /// </summary>
    private void ApplyPlaybackRate()
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;

        try
        {
            session.PlaybackRate = _playbackRate;
            RuntimeLog.Write("player", $"倍速 = {_playbackRate}x");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("player", $"设置倍速 {_playbackRate}x 失败：{ex.Message}");

            // 回滚到 1x，避免按钮显示 2.0x 而实际按原速播放
            if (Math.Abs(_playbackRate - 1.0) > 0.001)
            {
                _playbackRate = 1.0;
                UpdateSpeedUi();
            }

            ShowStatus($"当前视频不支持 {_playbackRate:0.##}x 倍速");
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1800);
                HideStatus();
            });
        }
    }

    private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
    {
        var duration = sender.NaturalDuration;
        var position = sender.Position;

        // 事件在后台线程触发，动 UI 要回到 UI 线程
        DispatcherQueue.TryEnqueue(() =>
        {
            double durationSeconds = duration.TotalSeconds;

            if (durationSeconds > 0 && Math.Abs(ProgressSlider.Maximum - durationSeconds) > 0.5)
            {
                _suppressProgress = true;
                ProgressSlider.Maximum = durationSeconds;
                _suppressProgress = false;
            }

            if (!_isSeeking)
            {
                _suppressProgress = true;
                ProgressSlider.Value = Math.Clamp(position.TotalSeconds, 0, ProgressSlider.Maximum);
                _suppressProgress = false;

                PositionText.Text = FormatTime(position);
                DurationText.Text = FormatTime(duration);
                UpdateMiniProgress(position.TotalSeconds, durationSeconds);
            }
        });
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        var state = sender.PlaybackState;

        DispatcherQueue.TryEnqueue(() =>
        {
            bool playing = state == MediaPlaybackState.Playing;
            PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";

            // 暂停时控制条常显，播放中才允许自动收起
            if (playing) RestartControlsTimer();
            else ShowControls(restartTimer: false);
        });
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isSeeking = true;

    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e) => CommitSeek();

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => CommitSeek();

    private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressProgress) return;

        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;

        // 键盘方向键 / 无障碍接口设置值时不经过 PointerPressed，直接跳转
        if (!_isSeeking)
        {
            var target = TimeSpan.FromSeconds(e.NewValue);
            session.Position = target;
            ResyncDanmakuCursor(target.TotalSeconds);
            PositionText.Text = FormatTime(target);
            DurationText.Text = FormatTime(session.NaturalDuration);
            return;
        }

        // 拖动过程中只更新时间预览与顶部气泡，松手才真正 seek
        var preview = TimeSpan.FromSeconds(e.NewValue);
        PositionText.Text = FormatTime(preview);
        DurationText.Text = FormatTime(session.NaturalDuration);
        ShowSeekBubble(preview, session.NaturalDuration);
    }

    private void CommitSeek()
    {
        if (!_isSeeking) return;
        _isSeeking = false;

        HideSeekBubble();

        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;

        var target = TimeSpan.FromSeconds(ProgressSlider.Value);
        session.Position = target;

        ResyncDanmakuCursor(target.TotalSeconds);
        PositionText.Text = FormatTime(target);
        DurationText.Text = FormatTime(session.NaturalDuration);
        ShowControls();
    }

    /// <summary>拖完进度条后把弹幕游标重新对齐，避免向前拖时漏放一屏弹幕</summary>
    private void ResyncDanmakuCursor(double seconds)
    {
        _danmakuCursor = _danmakus.FindIndex(d => d.TimeSeconds >= seconds);
        if (_danmakuCursor < 0) _danmakuCursor = _danmakus.Count;
        _lastPosition = -1;
        DanmakuControl.ClearAll();
    }

    private void PlayerHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _pointerInsidePlayer = true;

        // 窗口模式：指针在画面内就一直显示；全屏模式：显示后重新开始计时，
        // 这样「退出全屏」提示也会在几秒后自动消失，而不是一直糊在画面左上角
        ShowControls(restartTimer: _isFullscreen);
    }

    private void PlayerHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInsidePlayer = false;
        RestartControlsTimer();
    }

    private void ControlBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoveringControlBar = true;
        _controlsTimer.Stop();
    }

    private void ControlBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoveringControlBar = false;
        RestartControlsTimer();
    }

    private void ShowControls(bool restartTimer = true)
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            ControlsLayer.Visibility = Visibility.Visible;
            AnimateControls(show: true);
        }

        UpdateMiniProgressVisibility();
        if (restartTimer) RestartControlsTimer();
    }

    /// <summary>
    /// 控制层整体淡入淡出（附一点向上位移），对应 Bili.Copilot 的 0.2s Show/Hide 过渡。
    /// 收起动画结束后才真正 Collapsed，否则会闪一下。
    /// </summary>
    private void AnimateControls(bool show)
    {
        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(show ? 180 : 200));

        var fade = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = duration,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, ControlsLayer);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new DoubleAnimation
        {
            To = show ? 0 : 16,
            Duration = duration,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, ControlsTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        storyboard.Children.Add(slide);

        if (!show)
        {
            storyboard.Completed += (_, _) =>
            {
                if (!_controlsVisible) ControlsLayer.Visibility = Visibility.Collapsed;
            };
        }

        storyboard.Begin();
    }

    // ─────────────────────── 中部提示气泡 / 底部细进度 ───────────────────────

    private void ShowSeekBubble(TimeSpan position, TimeSpan duration)
    {
        SeekBubbleText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        SpeedBubble.Visibility = Visibility.Collapsed;
        SeekBubble.Visibility = Visibility.Visible;
    }

    private void HideSeekBubble() => SeekBubble.Visibility = Visibility.Collapsed;

    private async void ShowSpeedBubble(double rate)
    {
        SpeedBubbleText.Text = FormatRate(rate);
        SeekBubble.Visibility = Visibility.Collapsed;
        SpeedBubble.Visibility = Visibility.Visible;

        int token = ++_speedBubbleToken;
        await Task.Delay(2500);

        if (token == _speedBubbleToken) SpeedBubble.Visibility = Visibility.Collapsed;
    }

    private void UpdateMiniProgress(double position, double duration)
    {
        if (duration <= 0) return;

        MiniProgress.Maximum = duration;
        MiniProgress.Value = Math.Clamp(position, 0, duration);
    }

    /// <summary>控制层收起、且正在播放时，底部留一条细进度条（Bili.Copilot 的底部进度）</summary>
    private void UpdateMiniProgressVisibility()
    {
        bool playing = Player.MediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;
        MiniProgress.Visibility = !_controlsVisible && playing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 启动收起计时。窗口模式下指针停在画面里就常显；全屏模式下照常计时
    /// （否则点了全屏按钮后指针一直留在画面里，提示和进度条永远不会消失）。
    /// </summary>
    private void RestartControlsTimer()
    {
        _controlsTimer.Stop();

        if (_hoveringControlBar) return;
        if (!_isFullscreen && _pointerInsidePlayer) return;
        if (Player.MediaPlayer?.PlaybackSession?.PlaybackState != MediaPlaybackState.Playing) return;

        _controlsTimer.Start();
    }

    /// <summary>进入全屏后强制开始计时（此时指针正好停在刚点过的按钮上）</summary>
    private void StartControlsCountdown()
    {
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void HideControlsIfPlaying()
    {
        if (Player.MediaPlayer?.PlaybackSession?.PlaybackState != MediaPlaybackState.Playing) return;

        HideSeekBubble();
        _controlsVisible = false;
        AnimateControls(show: false);
        UpdateMiniProgressVisibility();
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    // ───────────────────────────── 弹幕 ─────────────────────────────

    private async Task LoadDanmakuAsync(string? videoId)
    {
        _danmakuTimer.Stop();
        _danmakus.Clear();
        _danmakuCursor = 0;
        _lastPosition = -1;
        DanmakuControl.ClearAll();

        if (string.IsNullOrWhiteSpace(videoId))
        {
            RuntimeLog.Write("danmaku", "videoId 为空，跳过取弹幕");
            return;
        }

        var list = await _danmaku.GetClientDanmakusAsync(videoId);
        _danmakus.AddRange(list);

        RuntimeLog.Write(
            "danmaku",
            $"videoId={videoId} 取到 {_danmakus.Count} 条，开关={AppSettings.Current.DanmakuEnabled}");

        if (AppSettings.Current.DanmakuEnabled && _danmakus.Count > 0)
        {
            _danmakuTimer.Start();
        }
    }

    private void DanmakuTimer_Tick(object? sender, object e)
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null || _danmakus.Count == 0) return;

        double position = session.Position.TotalSeconds;

        // 拖动进度条回退时重置游标
        if (_lastPosition >= 0 && position < _lastPosition - 1.5)
        {
            _danmakuCursor = 0;
            DanmakuControl.ClearAll();
        }

        _lastPosition = position;

        while (_danmakuCursor < _danmakus.Count && _danmakus[_danmakuCursor].TimeSeconds <= position)
        {
            var item = _danmakus[_danmakuCursor];

            // 刚跳转过来时不要补放一大堆积压弹幕
            if (position - item.TimeSeconds <= 1.5)
            {
                DanmakuControl.AddDanmaku(item.Text, item.FontSize, item.Color, item.Mode);
            }

            _danmakuCursor++;
        }
    }

    private void DanmakuToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = DanmakuToggle.IsChecked == true;
        DanmakuLayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        var settings = AppSettings.Current;
        settings.DanmakuEnabled = enabled;
        settings.Save();

        if (enabled && _danmakus.Count > 0) _danmakuTimer.Start();
        else
        {
            _danmakuTimer.Stop();
            DanmakuControl.ClearAll();
        }
    }

    // ───────────────────────────── 右栏切换 ─────────────────────────────

    private void SideTab_Click(object sender, RoutedEventArgs e)
    {
        int index = ReferenceEquals(sender, CommentTab) ? 1
            : ReferenceEquals(sender, RelatedTab) ? 2
            : 0;

        SelectSideTab(index);

        // 评论首次点开时才拉（进页面时已后台拉过一次，这里只兜底重试）
        if (index == 1 && !_commentsLoaded) _ = LoadCommentsAsync(append: false);
    }

    private void SelectSideTab(int index)
    {
        PlaylistTab.IsChecked = index == 0;
        CommentTab.IsChecked = index == 1;
        RelatedTab.IsChecked = index == 2;

        EpisodeList.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommentList.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        RelatedList.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        LoadMoreCommentButton.Visibility = index == 1 && Comments.Count > 0 && _commentPage < _commentTotalPage
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 标题跟着分页走，否则会出现「分P 16」这种评论数挂在分P标题上的错位
        ListTitle.Text = index switch
        {
            1 => "评论",
            2 => "相关视频",
            _ => _playlistTitle
        };

        ListCountText.Text = index switch
        {
            1 => _commentTotalCount > 0 ? VideoDetailService.FormatCount(_commentTotalCount) : Comments.Count.ToString(),
            2 => RelatedVideos.Count > 0 ? RelatedVideos.Count.ToString() : string.Empty,
            _ => Playlist.Count.ToString()
        };

        UpdateSideHint(index);
    }

    private void UpdateSideHint(int index)
    {
        string? hint = null;

        if (index == 0)
        {
            if (Playlist.Count == 0) hint = _playlistHint ?? (_isBangumi ? "没有取到剧集清单" : "该视频没有分P信息");
        }
        else if (index == 1)
        {
            if (!_commentsLoaded) hint = "正在加载评论…";
            else if (Comments.Count == 0) hint = "暂无评论";
        }
        else
        {
            if (RelatedVideos.Count == 0)
            {
                hint = _isBangumi ? "番剧暂不支持相关推荐" : "暂无相关视频";
            }
        }

        SideEmptyHint.Text = hint ?? string.Empty;
        SideEmptyHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────────────────────────── 评论 ─────────────────────────────

    private async Task LoadCommentsAsync(bool append)
    {
        // 番剧的评论 sourceType 与视频不同，实测传 2 拿不到就退回 3 再试
        int sourceType = _isBangumi ? 2 : 3;

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

        if (result.ErrorMessage is not null && Comments.Count == 0)
        {
            SideEmptyHint.Text = result.ErrorMessage;
            SideEmptyHint.Visibility = Visibility.Visible;
        }

        if (CommentTab.IsChecked == true) SelectSideTab(1);
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
            if (RelatedTab.IsChecked == true) SelectSideTab(2);
            return;
        }

        var list = await _related.GetRelatedAsync(_channelId, excludeDougaId: _resourceId, max: 20);

        RelatedVideos.Clear();
        foreach (var item in list) RelatedVideos.Add(item);

        if (RelatedTab.IsChecked == true) SelectSideTab(2);
    }

    private async void RelatedList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RelatedVideoItem item) return;
        if (string.IsNullOrWhiteSpace(item.DougaId)) return;

        // 同页内切换，不新建页面实例
        await LoadAsync(item.DougaId, 2, null, null, null);
        RootScroll.ChangeView(null, 0, null);
    }

    // ───────────────────────────── 视图辅助 ─────────────────────────────

    private void PlayerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlayerHeight();
        UpdateControlBreakpoints();
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
    private void Player_MediaOpened(MediaPlayer sender, object args)
    {
        // 事件在后台线程触发，动 UI 要回到 UI 线程
        DispatcherQueue.TryEnqueue(() =>
        {
            HideStatus();
            ShowControls(restartTimer: false);
            _ffmpegFallbackTried = false;

            // 换集 / 换源后倍速会被重置，重新贴回用户选的档位
            ApplyPlaybackRate();

            if (!AppSettings.Current.ResumePlayback || _pendingResumeSeconds <= 5) return;

            var session = Player.MediaPlayer?.PlaybackSession;
            if (session is null) return;

            // 只在进度明显早于续播点时跳转，避免片尾秒退
            if (session.NaturalDuration.TotalSeconds > _pendingResumeSeconds + 10)
            {
                session.Position = TimeSpan.FromSeconds(_pendingResumeSeconds);
                _danmakuCursor = 0;
                DanmakuControl.ClearAll();
            }
        });
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
