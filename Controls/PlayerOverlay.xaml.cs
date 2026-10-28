using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Linq;
using System.Threading.Tasks;
using AStation_Client.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace AStation_Client.Controls;

/// <summary>
/// 统一的播放器层：点播与直播共用同一套视频区、控制栏、顶栏、全屏与自动隐藏逻辑。
///
/// 只负责「播放器外壳」：取流、弹幕数据、评论等业务逻辑留在页面里，
/// 页面通过 <see cref="MediaElement"/> 塞 MediaSource / FFmpegInteropX 的结果。
///
/// 播放内核与代理链路完全沿用原实现（HLS 代理、FFmpegInteropX 兜底都在页面侧）。
/// </summary>
public sealed partial class PlayerOverlay : UserControl
{
    /// <summary>控制条自动隐藏计时器（播放中且鼠标离开画面 2.5 秒后收起）</summary>
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    private bool _controlsVisible = true;
    private bool _isFullscreen;
    private bool _pointerInsidePlayer;
    private bool _hoveringControlBar;

    private double _savedHeight;
    private double _aspectRatio = 16.0 / 9.0;
    private bool _liveMode;

    /// <summary>用户正按住进度条拖动（此时不接受播放器的进度回写）</summary>
    private bool _isSeeking;

    /// <summary>程序内部回写 Slider 时置位，避免触发 ValueChanged 里的 seek</summary>
    private bool _suppressProgress;

    /// <summary>倍速面板是否已真正打开（Flyout 内容延迟加载，打开前的 Value 回写要忽略）</summary>
    private bool _speedPanelReady;

    private bool _suppressSpeedChange;
    private int _speedBubbleToken;
    private double _playbackRate = 1.0;

    /// <summary>用户当前是否开着弹幕（控件自己维护，页面据此启停弹幕时钟）</summary>
    private bool _danmakuEnabled = true;

    /// <summary>控制层左信息位是否有副标题（没有就整块收起，断点判断也要跟着走）</summary>
    private bool _hasSubtitle;

    public PlayerOverlay()
    {
        InitializeComponent();

        // MediaPlayerElement 本身没有 MediaOpened / MediaFailed，要挂到内部 MediaPlayer 上
        Player.MediaPlayer.MediaOpened += Player_MediaOpened;
        Player.MediaPlayer.MediaFailed += Player_MediaFailed;

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

        ApplyDanmakuSettings();
        UpdateSpeedUi();
        UpdateVolumeIcon();

        // 顶栏常显（随控制层一起淡出）：窗口模式只有返回 + 标题，全屏时再多一个「退出全屏」
        TopBar.Visibility = Visibility.Visible;
        ExitFullscreenButton.Visibility = Visibility.Collapsed;
    }

    // ───────────────────────────── 对外接口 ─────────────────────────────

    /// <summary>真正的播放控件（页面负责设置 Source / AutoPlay）</summary>
    public MediaPlayerElement MediaElement => Player;

    /// <summary>内部弹幕层（页面往里推弹幕、调速度 / 不透明度）</summary>
    public DanmakuPanel Danmaku => DanmakuControl;

    public bool IsFullscreen => _isFullscreen;

    public bool IsLiveMode => _liveMode;

    /// <summary>封面比例，默认 16:9</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        set => _aspectRatio = value > 0 ? value : 16.0 / 9.0;
    }

    public double PlaybackRate => _playbackRate;

    public bool DanmakuEnabled => _danmakuEnabled;

    /// <summary>点播放器左上角的返回</summary>
    public event EventHandler? BackRequested;

    /// <summary>进入 / 退出全屏（页面据此收起右栏、改页面背景）</summary>
    public event EventHandler<bool>? FullscreenChanged;

    public event EventHandler? PrevRequested;

    public event EventHandler? NextRequested;

    /// <summary>跳转完成（含拖动、±10 秒、键盘），参数为目标秒数。页面用它重同步弹幕游标。</summary>
    public event EventHandler<double>? Seeked;

    /// <summary>弹幕开关变化</summary>
    public event EventHandler<bool>? DanmakuToggled;

    /// <summary>媒体就绪（转发内部 MediaPlayer.MediaOpened，已切回 UI 线程）</summary>
    public event EventHandler? MediaOpened;

    /// <summary>媒体失败（转发内部 MediaPlayer.MediaFailed）</summary>
    public event EventHandler<string>? MediaFailed;

    public event EventHandler<double>? PlaybackRateChanged;

    /// <summary>点播形态：进度条 / 倍速 / 上下集 / ±10 秒都可用</summary>
    public void ConfigureForVideo()
    {
        _liveMode = false;
        LiveBadge.Visibility = Visibility.Collapsed;
        ControlBar.Height = 160;
        ProgressRow.Visibility = Visibility.Visible;
        MiniProgress.Visibility = Visibility.Collapsed;
        SpeedButton.Visibility = Visibility.Visible;
        PrevButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        BackwardButton.Visibility = Visibility.Visible;
        ForwardButton.Visibility = Visibility.Visible;
    }

    /// <summary>直播形态：没有可拖的进度，控制条收成单行，保留音量 / 清晰度 / 弹幕 / 全屏</summary>
    public void ConfigureForLive()
    {
        _liveMode = true;
        LiveBadge.Visibility = Visibility.Visible;
        ControlBar.Height = 96;
        ProgressRow.Visibility = Visibility.Collapsed;
        MiniProgress.Visibility = Visibility.Collapsed;
        SpeedButton.Visibility = Visibility.Collapsed;
        PrevButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        BackwardButton.Visibility = Visibility.Collapsed;
        ForwardButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>设置标题条 / 控制层左信息位的文字</summary>
    public void SetTitle(string title, string? subtitle = null, string? rightText = null)
    {
        OverlayTitle.Text = title;
        PlayerTitleText.Text = title;
        PlayerSubText.Text = subtitle ?? string.Empty;
        OverlayEpisodeText.Text = rightText ?? string.Empty;

        _hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
        PlayerInfoSlot.Visibility = _hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
        UpdateControlBreakpoints();
    }

    public void SetQualityText(string text) => QualityText.Text = text;

    public void SetLineText(string text) => CdnText.Text = text;

    public void SetLineVisible(bool visible) =>
        CdnButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void SetQualityVisible(bool visible) =>
        QualityButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void SetSpeedVisible(bool visible) =>
        SpeedButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void SetEpisodeNavigation(bool prevEnabled, bool nextEnabled)
    {
        PrevButton.IsEnabled = prevEnabled;
        NextButton.IsEnabled = nextEnabled;
    }

    /// <summary>直播没有弹幕数据源时，把开关置灰并给出提示，而不是留一个点了没反应的按钮</summary>
    public void SetDanmakuAvailable(bool available, string? tip = null)
    {
        DanmakuToggle.IsEnabled = available;
        if (!string.IsNullOrWhiteSpace(tip))
        {
            ToolTipService.SetToolTip(DanmakuToggle, tip);
        }
    }

    public void AttachQualityContent(object content) => QualityHost.Content = content;

    public void AttachLineContent(object content) => LineHost.Content = content;

    public void ShowStatus(string message, bool spinner = true)
    {
        LoadingRing.IsActive = spinner;
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    /// <summary>换集 / 换源时把进度与播放按钮复位（否则会短暂显示上一集的进度）</summary>
    public void ResetProgress()
    {
        _suppressProgress = true;
        ProgressSlider.Maximum = 1;
        ProgressSlider.Value = 0;
        _suppressProgress = false;

        PositionText.Text = "00:00";
        DurationText.Text = "00:00";
        PlayPauseIcon.Glyph = "\uE768";

        MiniProgress.Maximum = 1;
        MiniProgress.Value = 0;
    }

    public void HideStatus()
    {
        LoadingRing.IsActive = false;
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    public void ShowControls(bool restartTimer = true)
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

    public void TogglePlayPause()
    {
        var mp = Player.MediaPlayer;
        if (mp?.PlaybackSession is null) return;

        if (mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) mp.Pause();
        else mp.Play();

        ShowControls();
    }

    public void SeekTo(double seconds, bool raiseEvent = true)
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;

        var duration = session.NaturalDuration;
        var target = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (duration > TimeSpan.Zero && target > duration) target = duration;

        session.Position = target;

        _suppressProgress = true;
        ProgressSlider.Value = target.TotalSeconds;
        _suppressProgress = false;

        PositionText.Text = FormatTime(target);
        DurationText.Text = FormatTime(duration);

        if (raiseEvent) Seeked?.Invoke(this, target.TotalSeconds);
    }

    public void SetPlaybackRate(double rate)
    {
        _playbackRate = rate;
        ApplyPlaybackRate();
        UpdateSpeedUi();
        ShowSpeedBubble(rate);
        PlaybackRateChanged?.Invoke(this, rate);
    }

    // ───────────────────────────── 全屏 ─────────────────────────────

    public void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    public void EnterFullscreen()
    {
        if (_isFullscreen) return;

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
        _savedHeight = PlayerHost.Height;

        // 用 Win32 直接做无边框全屏：AppWindow 的 FullScreen 呈现器在这台系统上
        // 会给客户区留 1px 边框，屏幕顶部就多一条白线
        var window = App.MainWindowInstance;
        RuntimeLog.Write("fullscreen", $"进入全屏：MainWindowInstance={(window is null ? "null" : "ok")}");

        // 顺序要紧：先收起外壳（改标题栏行高会让 WinUI 重新套一遍窗口样式），
        // 再把窗口样式改成 WS_POPUP 并铺满显示器，否则刚去掉的边框又会被加回来
        window?.SetChromeVisible(false);
        window?.EnterBorderlessFullscreen();

        PlayerHost.CornerRadius = new CornerRadius(0);
        FullscreenIcon.Glyph = "\uE73F";
        ExitFullscreenButton.Visibility = Visibility.Visible;

        // 全屏后可用区域是整个屏幕，播放器铺满
        PlayerHost.Height = XamlRoot?.Size.Height > 0 ? XamlRoot.Size.Height : _savedHeight;

        ShowControls();
        FullscreenChanged?.Invoke(this, true);

        // 布局落定后记录一次真实几何，白边问题排查全靠这行
        DispatcherQueue.TryEnqueue(LogFullscreenGeometry);
        _ = LogFullscreenGeometryLaterAsync();
        StartControlsCountdownDelayed();
    }

    public void ExitFullscreen()
    {
        if (!_isFullscreen) return;
        _isFullscreen = false;

        App.MainWindowInstance?.ExitBorderlessFullscreen();
        App.MainWindowInstance?.SetChromeVisible(true);

        PlayerHost.CornerRadius = new CornerRadius(10);
        FullscreenIcon.Glyph = "\uE740";
        ExitFullscreenButton.Visibility = Visibility.Collapsed;

        _controlsTimer.Stop();
        ShowControls(restartTimer: false);

        FullscreenChanged?.Invoke(this, false);

        // 退出全屏后窗口尺寸变化是异步的，下一帧再按 16:9 重算
        DispatcherQueue.TryEnqueue(UpdatePlayerHeight);
    }

    /// <summary>进入全屏后强制开始计时（此时指针正好停在刚点过的按钮上）</summary>
    private async void StartControlsCountdownDelayed()
    {
        await Task.Delay(700);
        if (!_isFullscreen) return;

        // 进全屏时窗口尺寸变了，控制条上的 PointerExited 有可能收不到，
        // hover 状态会卡在 true，计时器就永远不启动 —— 这里先复位一次
        _hoveringControlBar = false;
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void LogFullscreenGeometry()
    {
        try
        {
            var root = XamlRoot?.Size ?? new Windows.Foundation.Size(0, 0);
            var origin = PlayerHost.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            RuntimeLog.Write(
                "fullscreen",
                $"全屏几何：player=({origin.X:F0},{origin.Y:F0}) {PlayerHost.ActualWidth:F0}x{PlayerHost.ActualHeight:F0} " +
                $"xamlroot={root.Width:F0}x{root.Height:F0}");
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

    // ───────────────────────────── 尺寸 ─────────────────────────────

    private void PlayerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlayerHeight();
        UpdateControlBreakpoints();
    }

    private void UpdatePlayerHeight()
    {
        if (_isFullscreen) return;

        // 宽度取 UserControl 自己的：换源瞬间 PlayerHost 可能被内容塌成 0 高，
        // 但它作为 StackPanel 的子项，横向宽度始终是父给的可用宽度
        double width = ActualWidth > 0 ? ActualWidth : PlayerHost.ActualWidth;
        if (width <= 0) return;

        double target = Math.Round(width / _aspectRatio);
        if (target < 1) return;

        // Height 初始是 NaN（Auto），NaN 参与比较永远为 false —— 必须先单独判掉，
        // 否则换清晰度后内容塌陷就再也修正不回来
        if (double.IsNaN(PlayerHost.Height) || Math.Abs(PlayerHost.Height - target) >= 1)
        {
            PlayerHost.Height = target;
        }
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
        PlayerInfoSlot.Visibility = _hasSubtitle && width >= 1000 ? Visibility.Visible : Visibility.Collapsed;

        // 音量滑杆 80px 是右列里最占位的，再窄就只留图标按钮
        VolumeSlider.Visibility = width >= 880 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── 控制层（顶栏 + 控制条 + 气泡） ─────────────────────────

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void SkipBackward_Click(object sender, RoutedEventArgs e) => SeekRelative(-10);

    private void SkipForward_Click(object sender, RoutedEventArgs e) => SeekRelative(10);

    private void SeekRelative(double seconds)
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;

        SeekTo((session.Position + TimeSpan.FromSeconds(seconds)).TotalSeconds);
        ShowControls();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke(this, EventArgs.Empty);

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

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

    /// <summary>
    /// 把当前倍速贴到播放会话上。换集 / 换源后 PlaybackRate 会被重置，
    /// 所以 MediaOpened 里也要再调一次。部分源（如 FFmpeg 软解、直播）不支持变速，
    /// 这里只记日志并回滚，不让它抛到 UI 线程。
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

            if (Math.Abs(_playbackRate - 1.0) > 0.001)
            {
                _playbackRate = 1.0;
                UpdateSpeedUi();
            }

            ShowStatus($"当前视频不支持 {_playbackRate:0.##}x 倍速", spinner: false);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1800);
                HideStatus();
            });
        }
    }

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

    // ───────────────────────────── 播放状态 / 进度 ─────────────────────────────

    private void Player_MediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            HideStatus();
            ShowControls(restartTimer: false);

            // 换集 / 换源后倍速会被重置，重新贴回用户选的档位
            ApplyPlaybackRate();
            MediaOpened?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? args.Error.ToString()
            : args.ErrorMessage;

        RuntimeLog.Write("play", $"系统解码失败：{args.Error} / {message}");
        DispatcherQueue.TryEnqueue(() => MediaFailed?.Invoke(this, message));
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
            PositionText.Text = FormatTime(target);
            DurationText.Text = FormatTime(session.NaturalDuration);
            Seeked?.Invoke(this, target.TotalSeconds);
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
        SeekTo(ProgressSlider.Value);
        ShowControls();
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
        if (_liveMode) return;

        SpeedBubbleText.Text = FormatRate(rate);
        SeekBubble.Visibility = Visibility.Collapsed;
        SpeedBubble.Visibility = Visibility.Visible;

        int token = ++_speedBubbleToken;
        await Task.Delay(2500);

        if (token == _speedBubbleToken) SpeedBubble.Visibility = Visibility.Collapsed;
    }

    private void UpdateMiniProgress(double position, double duration)
    {
        if (_liveMode || duration <= 0) return;

        MiniProgress.Maximum = duration;
        MiniProgress.Value = Math.Clamp(position, 0, duration);
    }

    /// <summary>控制层收起、且正在播放时，底部留一条细进度条（Bili.Copilot 的底部进度）</summary>
    private void UpdateMiniProgressVisibility()
    {
        if (_liveMode)
        {
            MiniProgress.Visibility = Visibility.Collapsed;
            return;
        }

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

    // ───────────────────────────── 弹幕开关 ─────────────────────────────

    private void ApplyDanmakuSettings()
    {
        var settings = AppSettings.Current;
        DanmakuControl.SpeedSeconds = settings.DanmakuSpeedSeconds;
        DanmakuControl.AreaRatio = settings.DanmakuArea;
        DanmakuControl.DanmakuOpacity = settings.DanmakuOpacity;

        _danmakuEnabled = settings.DanmakuEnabled;
        DanmakuToggle.IsChecked = _danmakuEnabled;
        DanmakuLayer.Visibility = _danmakuEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DanmakuToggle_Click(object sender, RoutedEventArgs e)
    {
        _danmakuEnabled = DanmakuToggle.IsChecked == true;
        DanmakuLayer.Visibility = _danmakuEnabled ? Visibility.Visible : Visibility.Collapsed;

        var settings = AppSettings.Current;
        settings.DanmakuEnabled = _danmakuEnabled;
        settings.Save();

        DanmakuToggled?.Invoke(this, _danmakuEnabled);
    }
}
