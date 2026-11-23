using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using AStation_Client.Services;

namespace AStation_Client.Controls;

/// <summary>
/// 弹幕渲染面板。
///
/// 运动全部交给合成器（Composition）：每条弹幕进场时就算好起止 X 与时长，
/// 用 ScalarKeyFrameAnimation 让合成线程逐帧推进，UI 线程零每帧工作
///（原来 16ms DispatcherTimer 每帧对每条弹幕写 TranslateTransform.X，N 条弹幕 = N 次渲染失效）。
/// 面板上只剩一个 250ms 的低频清理定时器，负责移除跑完 / 到寿的弹幕。
///
/// 为什么不用 Storyboard：WinUI 3 里用 Storyboard 去动画 TranslateTransform 并不可靠
///（实测不插值、Completed 立即触发）；Visual 层动画不受此限制。
/// </summary>
public sealed partial class DanmakuPanel : UserControl
{
    private readonly List<ActiveDanmaku> _active = new();
    private readonly DispatcherTimer _cleanupTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _clock = new();

    /// <summary>顶部安全区高度（播放器右上角有清晰度 / 弹幕开关）</summary>
    private const double TopSafeInset = 48;

    /// <summary>固定弹幕（顶部 / 底部）停留与淡出时长</summary>
    private const double FixedLifeSeconds = 3.2;
    private const double FixedFadeSeconds = 0.8;

    private int _nextLane;
    private double _opacity = 0.9;
    private bool _sizeLogged;

    public DanmakuPanel()
    {
        InitializeComponent();
        _cleanupTimer.Tick += OnCleanupTick;
        Unloaded += (_, _) => _cleanupTimer.Stop();
    }

    /// <summary>弹幕横穿屏幕所需秒数（越小越快）</summary>
    public double SpeedSeconds { get; set; } = 9;

    /// <summary>弹幕出现区域占画面高度比例（0.25~1）</summary>
    public double AreaRatio { get; set; } = 0.75;

    /// <summary>弹幕整体不透明度</summary>
    public double DanmakuOpacity
    {
        get => _opacity;
        set
        {
            _opacity = value;
            DanmakuCanvas.Opacity = value;
        }
    }

    /// <summary>
    /// 添加一条弹幕。
    /// </summary>
    /// <param name="text">弹幕文本</param>
    /// <param name="fontSize">基准字号（会按播放器宽度缩放）</param>
    /// <param name="color">颜色，支持 "#FFFFFF" 或十进制 "16777215"</param>
    /// <param name="mode">1=滚动 4=底部 5=顶部</param>
    public void AddDanmaku(string text, double fontSize = 24, string? color = null, DanmakuMode mode = DanmakuMode.Scroll)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        double canvasWidth = DanmakuCanvas.ActualWidth > 0 ? DanmakuCanvas.ActualWidth : 960;
        double canvasHeight = DanmakuCanvas.ActualHeight > 0 ? DanmakuCanvas.ActualHeight : 540;

        if (!_sizeLogged)
        {
            _sizeLogged = true;
            RuntimeLog.Write("danmaku", $"画布实际尺寸 {canvasWidth:F0}x{canvasHeight:F0}");
        }

        // 字号随画面宽度缩放，避免小窗口弹幕过大
        double scale = Math.Clamp(canvasWidth / 1100.0, 0.6, 1.8);
        double size = Math.Clamp(fontSize * scale, 12, 40);

        var element = BuildDanmakuElement(text, size, ParseColor(color));

        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = element.DesiredSize.Width;
        double textHeight = element.DesiredSize.Height;

        double laneHeight = textHeight + 6;

        // 顶部给清晰度 / 弹幕开关留一条安全边距，否则弹幕从右上角进场会被控件压住
        double topInset = Math.Min(TopSafeInset, canvasHeight * 0.2);

        double usableHeight = Math.Max(laneHeight, (canvasHeight - topInset) * Math.Clamp(AreaRatio, 0.2, 1.0));
        int laneCount = Math.Max(1, (int)(usableHeight / laneHeight));
        int lane = _nextLane++ % laneCount;

        bool isFixed = mode is DanmakuMode.Top or DanmakuMode.Bottom;

        double startX;
        double endX = 0;
        double durationSeconds = 0;

        if (isFixed)
        {
            double y = mode == DanmakuMode.Top
                ? topInset + 8 + lane * laneHeight
                : Math.Max(topInset + 8, canvasHeight - 8 - (lane + 1) * laneHeight);

            Canvas.SetTop(element, y);

            // 固定弹幕不动：位置用 RenderTransform 一次定位（不走动画，也就没有每帧成本）
            startX = Math.Max(0, (canvasWidth - textWidth) / 2);
            element.RenderTransform = new TranslateTransform { X = startX };
        }
        else
        {
            Canvas.SetTop(element, topInset + 8 + lane * laneHeight);

            // 从右边缘外开始，完整穿出左边缘结束（与旧实现同一条速度曲线）
            startX = canvasWidth + 8;
            endX = -textWidth - 24;

            double distance = startX - endX;
            double speed = Math.Max(20, distance / Math.Clamp(SpeedSeconds, 3, 30));
            durationSeconds = distance / speed;
        }

        Canvas.SetLeft(element, 0);
        DanmakuCanvas.Children.Add(element);

        // 进可视树之后再取 Visual，把运动挂给合成线程
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var entry = new ActiveDanmaku
        {
            Element = element,
            Visual = visual,
            IsFixed = isFixed,
            ExpiresAt = Now() + (isFixed ? FixedLifeSeconds : durationSeconds)
        };

        if (isFixed)
        {
            // 停留期末尾 0.8 秒淡出（延迟与时长都在合成器上，UI 线程不参与）
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 1f);
            fade.InsertKeyFrame(1f, 0f);
            fade.DelayTime = TimeSpan.FromSeconds(FixedLifeSeconds - FixedFadeSeconds);
            fade.Duration = TimeSpan.FromSeconds(FixedFadeSeconds);
            visual.StartAnimation("Opacity", fade);
        }
        else
        {
            var slide = compositor.CreateScalarKeyFrameAnimation();
            slide.InsertKeyFrame(0f, (float)startX);
            slide.InsertKeyFrame(1f, (float)endX);
            slide.Duration = TimeSpan.FromSeconds(durationSeconds);
            visual.StartAnimation("Offset.X", slide);
        }

        _active.Add(entry);
        EnsureCleaning();
    }

    /// <summary>
    /// 构造一条弹幕的视觉元素。
    /// 纯文字走 TextBlock（绝大多数弹幕，创建便宜、测量快）；
    /// 带 <c>[emot=acfun,1738/]</c> 这类表情码时才用 RichTextBlock 组装富文本，
    /// 否则表情码会原样显示成一串方括号。
    /// </summary>
    private FrameworkElement BuildDanmakuElement(string text, double size, Brush brush)
    {
        var weight = Microsoft.UI.Text.FontWeights.SemiBold;

        if (!UbbParser.HasMarkup(text))
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = weight,
                Foreground = brush,
                IsHitTestVisible = false,
                TextWrapping = TextWrapping.NoWrap
            };
        }

        // 字典还没加载完时，先按纯文本显示（表情会退成「[表情]」），
        // 不为了等网络把弹幕放慢——弹幕是时间敏感的。
        var rich = new RichTextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.NoWrap
        };

        var paragraph = new Paragraph();
        foreach (var segment in UbbParser.Parse(text))
        {
            switch (segment.Kind)
            {
                case UbbSegmentKind.Text:
                    paragraph.Inlines.Add(new Run { Text = segment.Text, FontSize = size, FontWeight = weight });
                    break;

                case UbbSegmentKind.Emote when !string.IsNullOrWhiteSpace(segment.Url):
                    paragraph.Inlines.Add(new InlineUIContainer
                    {
                        Child = new Image
                        {
                            Source = new BitmapImage(new Uri(segment.Url!)),
                            Height = Math.Round(size * 1.3),
                            Margin = new Thickness(1, 0, 1, -2),
                            VerticalAlignment = VerticalAlignment.Center,
                            IsHitTestVisible = false
                        }
                    });
                    break;

                // 弹幕不显示图片评论，直接跳过
            }
        }

        if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new Run { Text = text, FontSize = size });
        rich.Blocks.Add(paragraph);
        return rich;
    }

    /// <summary>面板时钟（秒）。首次取用时启动，供 ExpiresAt 计算</summary>
    private double Now()
    {
        if (!_clock.IsRunning) _clock.Start();
        return _clock.Elapsed.TotalSeconds;
    }

    private void EnsureCleaning()
    {
        Now();

        if (_cleanupTimer.IsEnabled) return;
        _cleanupTimer.Start();
    }

    private void OnCleanupTick(object? sender, object e)
    {
        if (_active.Count == 0)
        {
            _cleanupTimer.Stop();
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;

        // 只做簿记：移除跑完 / 到寿的弹幕。运动与淡出都在合成线程，这里没有逐帧渲染工作
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].ExpiresAt > now) continue;
            RemoveAt(i);
        }
    }

    private void RemoveAt(int index)
    {
        var entry = _active[index];

        entry.Visual.StopAnimation("Offset.X");
        entry.Visual.StopAnimation("Opacity");

        if (DanmakuCanvas.Children.Contains(entry.Element))
        {
            DanmakuCanvas.Children.Remove(entry.Element);
        }

        _active.RemoveAt(index);
    }

    /// <summary>清空所有弹幕</summary>
    public void ClearAll()
    {
        foreach (var entry in _active)
        {
            entry.Visual.StopAnimation("Offset.X");
            entry.Visual.StopAnimation("Opacity");
        }

        _active.Clear();
        DanmakuCanvas.Children.Clear();
        _nextLane = 0;
        _cleanupTimer.Stop();
        _clock.Reset();
    }

    private static Brush ParseColor(string? color)
    {
        var white = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

        if (string.IsNullOrWhiteSpace(color)) return white;

        try
        {
            var text = color.Trim();

            // 十进制颜色值，如 16777215
            if (long.TryParse(text, out long decimalColor))
            {
                uint rgb = (uint)decimalColor;
                return new SolidColorBrush(Windows.UI.Color.FromArgb(
                    255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
            }

            // "#RRGGBB"
            if (text.StartsWith('#')) text = text[1..];
            if (text.Length == 6 &&
                byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(text[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            }
        }
        catch
        {
            // 解析失败用白色
        }

        return white;
    }

    private sealed class ActiveDanmaku
    {
        public FrameworkElement Element { get; set; } = null!;

        /// <summary>元素的合成 Visual（运动 / 淡出动画挂在它上面）</summary>
        public Visual Visual { get; set; } = null!;

        public bool IsFixed { get; set; }

        /// <summary>移除时刻（面板时钟，秒）：滚动弹幕 = 进场 + 穿屏时长；固定弹幕 = 进场 + 停留时长</summary>
        public double ExpiresAt { get; set; }
    }
}
