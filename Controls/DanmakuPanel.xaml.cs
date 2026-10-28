using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using AStation_Client.Services;

namespace AStation_Client.Controls;

/// <summary>
/// 弹幕渲染面板。
///
/// 运动由面板自己的定时器逐帧推进（不用 Storyboard）：
/// WinUI 3 里用 Storyboard 去动画 <c>TranslateTransform.X</c> 并不可靠 ——
/// 实测动画不会真正插值，Completed 会立刻触发，元素还没画出来就被移除了。
/// 自己算 x 反而更稳，也方便做「按轨道避重叠」和暂停 / 变速。
/// </summary>
public sealed partial class DanmakuPanel : UserControl
{
    private readonly List<ActiveDanmaku> _active = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();

    /// <summary>顶部安全区高度（播放器右上角有清晰度 / 弹幕开关）</summary>
    private const double TopSafeInset = 48;

    private int _nextLane;
    private double _opacity = 0.9;
    private bool _sizeLogged;
    private TimeSpan _lastTick;

    public DanmakuPanel()
    {
        InitializeComponent();
        _timer.Tick += OnTimerTick;
        Unloaded += (_, _) => _timer.Stop();
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

        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ParseColor(color),
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.NoWrap
        };

        textBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = textBlock.DesiredSize.Width;
        double textHeight = textBlock.DesiredSize.Height;

        double laneHeight = textHeight + 6;

        // 顶部给清晰度 / 弹幕开关留一条安全边距，否则弹幕从右上角进场会被控件压住
        double topInset = Math.Min(TopSafeInset, canvasHeight * 0.2);

        double usableHeight = Math.Max(laneHeight, (canvasHeight - topInset) * Math.Clamp(AreaRatio, 0.2, 1.0));
        int laneCount = Math.Max(1, (int)(usableHeight / laneHeight));
        int lane = _nextLane++ % laneCount;

        var transform = new TranslateTransform();
        textBlock.RenderTransform = transform;

        var entry = new ActiveDanmaku
        {
            Element = textBlock,
            Transform = transform,
            Width = textWidth,
            IsFixed = mode is DanmakuMode.Top or DanmakuMode.Bottom,
            Mode = mode
        };

        if (entry.IsFixed)
        {
            double y = mode == DanmakuMode.Top
                ? topInset + 8 + lane * laneHeight
                : Math.Max(topInset + 8, canvasHeight - 8 - (lane + 1) * laneHeight);

            Canvas.SetTop(textBlock, y);
            entry.X = Math.Max(0, (canvasWidth - textWidth) / 2);
            entry.LifeLeft = 3.2;
            transform.X = entry.X;
        }
        else
        {
            Canvas.SetTop(textBlock, topInset + 8 + lane * laneHeight);

            // 从右边缘外开始，完整穿出左边缘结束
            entry.X = canvasWidth + 8;
            entry.EndX = -textWidth - 24;
            entry.Speed = Math.Max(20, (entry.X - entry.EndX) / Math.Clamp(SpeedSeconds, 3, 30));
            transform.X = entry.X;
        }

        Canvas.SetLeft(textBlock, 0);
        DanmakuCanvas.Children.Add(textBlock);
        _active.Add(entry);

        EnsureTicking();
    }

    private void EnsureTicking()
    {
        if (_timer.IsEnabled) return;

        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, object e)
    {
        var now = _clock.Elapsed;
        double delta = Math.Clamp((now - _lastTick).TotalSeconds, 0, 0.25);
        _lastTick = now;

        if (_active.Count == 0)
        {
            _timer.Stop();
            return;
        }

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var entry = _active[i];

            if (entry.IsFixed)
            {
                entry.LifeLeft -= delta;

                // 最后 0.8 秒淡出
                if (entry.LifeLeft < 0.8)
                {
                    entry.Element.Opacity = Math.Max(0, entry.LifeLeft / 0.8);
                }

                if (entry.LifeLeft <= 0) RemoveAt(i);
                continue;
            }

            entry.X -= entry.Speed * delta;
            entry.Transform.X = entry.X;

            if (entry.X <= entry.EndX) RemoveAt(i);
        }
    }

    private void RemoveAt(int index)
    {
        var entry = _active[index];

        if (DanmakuCanvas.Children.Contains(entry.Element))
        {
            DanmakuCanvas.Children.Remove(entry.Element);
        }

        _active.RemoveAt(index);
    }

    /// <summary>清空所有弹幕</summary>
    public void ClearAll()
    {
        _active.Clear();
        DanmakuCanvas.Children.Clear();
        _nextLane = 0;
        _timer.Stop();
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
        public TextBlock Element { get; set; } = null!;
        public TranslateTransform Transform { get; set; } = null!;

        /// <summary>当前 X 偏移</summary>
        public double X { get; set; }

        /// <summary>滚动弹幕的终点 X</summary>
        public double EndX { get; set; }

        /// <summary>滚动速度（像素 / 秒）</summary>
        public double Speed { get; set; }

        /// <summary>文本宽度</summary>
        public double Width { get; set; }

        /// <summary>固定弹幕剩余显示秒数</summary>
        public double LifeLeft { get; set; }

        public bool IsFixed { get; set; }

        public DanmakuMode Mode { get; set; }
    }
}
