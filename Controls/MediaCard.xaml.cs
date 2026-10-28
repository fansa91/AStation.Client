using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AStation_Client.Controls;

/// <summary>
/// 统一的媒体卡片：视频 / 番剧 / 直播共用一套骨架，靠 <see cref="CoverAspect"/> 换封面比例、
/// 靠三个角标字段换信息密度。宽度由父容器（UniformGridLayout）拉伸决定，封面高度跟随宽度算。
/// </summary>
public sealed partial class MediaCard : UserControl
{
    public MediaCard()
    {
        InitializeComponent();
    }

    /// <summary>封面图地址；留空时显示底色占位</summary>
    public string Cover
    {
        get => (string)GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }
    public static readonly DependencyProperty CoverProperty =
        DependencyProperty.Register(nameof(Cover), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty));

    /// <summary>副标题：作者 / 主播</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty, OnMetaChanged));

    /// <summary>第二段元信息：播放量 / 更新时间</summary>
    public string Meta
    {
        get => (string)GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }
    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(nameof(Meta), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty, OnMetaChanged));

    /// <summary>右上角标（时长 / 在线人数）</summary>
    public string TopRightBadge
    {
        get => (string)GetValue(TopRightBadgeProperty);
        set => SetValue(TopRightBadgeProperty, value);
    }
    public static readonly DependencyProperty TopRightBadgeProperty =
        DependencyProperty.Register(nameof(TopRightBadge), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty, OnTopRightBadgeChanged));

    /// <summary>左下角标（分区 / 集数）</summary>
    public string BottomLeftBadge
    {
        get => (string)GetValue(BottomLeftBadgeProperty);
        set => SetValue(BottomLeftBadgeProperty, value);
    }
    public static readonly DependencyProperty BottomLeftBadgeProperty =
        DependencyProperty.Register(nameof(BottomLeftBadge), typeof(string), typeof(MediaCard), new PropertyMetadata(string.Empty, OnBottomLeftBadgeChanged));

    /// <summary>是否显示红色 LIVE 角标</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }
    public static readonly DependencyProperty IsLiveProperty =
        DependencyProperty.Register(nameof(IsLive), typeof(bool), typeof(MediaCard), new PropertyMetadata(false, OnIsLiveChanged));

    /// <summary>封面宽高比（宽 / 高）：16:9 传 1.778，3:4 竖版封面传 0.75</summary>
    public double CoverAspect
    {
        get => (double)GetValue(CoverAspectProperty);
        set => SetValue(CoverAspectProperty, value);
    }
    public static readonly DependencyProperty CoverAspectProperty =
        DependencyProperty.Register(nameof(CoverAspect), typeof(double), typeof(MediaCard), new PropertyMetadata(16.0 / 9.0, OnCoverAspectChanged));

    /// <summary>
    /// 点击时带出去的载荷（视频 id / 直播间对象 / 番剧对象）。
    /// 单独开一个属性而不是复用 FrameworkElement.Tag，避免和业务模型上的 Tag 字段撞名。
    /// </summary>
    public object? Payload
    {
        get => GetValue(PayloadProperty);
        set => SetValue(PayloadProperty, value);
    }
    public static readonly DependencyProperty PayloadProperty =
        DependencyProperty.Register(nameof(Payload), typeof(object), typeof(MediaCard), new PropertyMetadata(null));

    /// <summary>卡片点击。载荷在 <see cref="Payload"/> 里</summary>
    public event EventHandler<RoutedEventArgs>? Click;

    // ───────────────────────── 内部联动 ─────────────────────────

    private double _coverAspect = 16.0 / 9.0;

    private static void OnCoverAspectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MediaCard)d;
        card._coverAspect = (double)e.NewValue > 0 ? (double)e.NewValue : 16.0 / 9.0;
        card.UpdateCoverHeight();
    }

    private static void OnMetaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MediaCard)d).UpdateMetaRow();
    }

    /// <summary>
    /// 副标题和元信息只有「两段都在」时才插分隔点，否则会出现「· 作者」这种前导点。
    /// </summary>
    private void UpdateMetaRow()
    {
        bool hasSubtitle = !string.IsNullOrWhiteSpace(Subtitle);
        bool hasMeta = !string.IsNullOrWhiteSpace(Meta);

        MetaDot.Visibility = hasSubtitle && hasMeta ? Visibility.Visible : Visibility.Collapsed;
        MetaText.Visibility = hasMeta ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Visibility = hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnTopRightBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MediaCard)d;
        var text = e.NewValue as string;
        card.TopRightBadgeText.Text = text ?? string.Empty;
        card.TopRightBadgeBox.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnBottomLeftBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MediaCard)d;
        var text = e.NewValue as string;
        card.BottomLeftBadgeText.Text = text ?? string.Empty;
        card.BottomLeftBadgeBox.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnIsLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MediaCard)d;
        card.LiveBadgeBox.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 封面高度 = 可用宽度 / 宽高比。宽度由父容器给，卡片自己不改宽度，
    /// 这样同一行卡片封面等高、标题行底部对齐。
    /// </summary>
    private void UpdateCoverHeight()
    {
        double width = ContentGrid.ActualWidth - ContentGrid.Padding.Left - ContentGrid.Padding.Right;
        if (width <= 0) return;

        double target = Math.Round(width / _coverAspect);
        if (target < 1) return;
        if (double.IsNaN(CoverRow.Height.Value) || Math.Abs(CoverRow.Height.Value - target) >= 0.5)
        {
            CoverRow.Height = new GridLength(target);
        }
    }

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCoverHeight();

    private void Root_Tapped(object sender, TappedRoutedEventArgs e) => Click?.Invoke(this, e);

    private void Grid_PointerEntered(object sender, PointerRoutedEventArgs e) => HoverLayer.Opacity = 1;

    private void Grid_PointerExited(object sender, PointerRoutedEventArgs e) => HoverLayer.Opacity = 0;
}
