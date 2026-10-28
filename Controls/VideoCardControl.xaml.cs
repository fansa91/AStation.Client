using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using AStation_Client.Models;

namespace AStation_Client.Controls;

public sealed partial class VideoCardControl : UserControl
{
    public VideoCardControl()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(VideoCardControl), new PropertyMetadata(string.Empty));

    public string Author
    {
        get => (string)GetValue(AuthorProperty);
        set => SetValue(AuthorProperty, value);
    }
    public static readonly DependencyProperty AuthorProperty =
        DependencyProperty.Register(nameof(Author), typeof(string), typeof(VideoCardControl), new PropertyMetadata(string.Empty));

    public string ViewCount
    {
        get => (string)GetValue(ViewCountProperty);
        set => SetValue(ViewCountProperty, value);
    }
    public static readonly DependencyProperty ViewCountProperty =
        DependencyProperty.Register(nameof(ViewCount), typeof(string), typeof(VideoCardControl), new PropertyMetadata(string.Empty));

    public string Duration
    {
        get => (string)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }
    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(string), typeof(VideoCardControl), new PropertyMetadata(string.Empty));

    public string Thumbnail
    {
        get => (string)GetValue(ThumbnailProperty);
        set => SetValue(ThumbnailProperty, value);
    }
    public static readonly DependencyProperty ThumbnailProperty =
        DependencyProperty.Register(nameof(Thumbnail), typeof(string), typeof(VideoCardControl), new PropertyMetadata(string.Empty));

    public string ResourceId
    {
        get => (string)GetValue(ResourceIdProperty);
        set => SetValue(ResourceIdProperty, value);
    }
    public static readonly DependencyProperty ResourceIdProperty =
        DependencyProperty.Register(nameof(ResourceId), typeof(string), typeof(VideoCardControl), new PropertyMetadata(string.Empty));

    /// <summary>
    /// 卡片点击事件，携带 RoutedEventArgs（兼容 XAML 绑定）
    /// </summary>
    public event EventHandler<RoutedEventArgs>? Click;

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Click?.Invoke(this, e);
    }

    /// <summary>
    /// 卡片宽度由父容器拉伸决定，缩略图按 16:9 跟随宽度算高，
    /// 这样卡片能填满整行而缩略图不变形（不会因为拉宽而被裁成条状）。
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        if (width <= 0) return;

        double target = width * 9.0 / 16.0;
        if (Math.Abs(ThumbnailRow.Height.Value - target) > 0.5)
        {
            ThumbnailRow.Height = new GridLength(target);
        }
    }
}
