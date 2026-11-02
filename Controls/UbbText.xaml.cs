using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using AStation_Client.Services;

namespace AStation_Client.Controls;

/// <summary>
/// 评论正文控件：解析 UBB（表情 / @ / 图片 / 换行）后渲染成富文本。
///
/// 为什么不用 TextBlock：正文里的表情是图片，需要塞进文字流里，
/// 只有 RichTextBlock 的 Paragraph 支持 <see cref="InlineUIContainer"/>。
/// 表情字典（<see cref="EmoteCatalog"/>）首次加载需要一次网络请求，
/// 未就绪时先用「[表情]」占位渲染，字典到位后再重画一次，避免整块空白。
/// </summary>
public sealed partial class UbbText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(UbbText),
        new PropertyMetadata(string.Empty, OnRenderPropertyChanged));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines),
        typeof(int),
        typeof(UbbText),
        new PropertyMetadata(0, OnMaxLinesChanged));

    public UbbText()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyForeground();
            if (!_catalogReady) _ = LoadCatalogAndRefreshAsync();
        };

        // Foreground 在 UserControl 上是现成的 DP，不能重定义，只能跟着转发给 RichTextBlock
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => ApplyForeground());
    }

    private void ApplyForeground()
    {
        if (Foreground is Brush brush) Root.Foreground = brush;
    }

    private bool _catalogReady;

    /// <summary>UBB 正文</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>最多显示几行，0 表示不限制</summary>
    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    private static void OnRenderPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UbbText control) control.Render();
    }

    private static void OnMaxLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UbbText control) control.Root.MaxLines = (int)e.NewValue;
    }

    private async Task LoadCatalogAndRefreshAsync()
    {
        try
        {
            await EmoteCatalog.Shared.EnsureLoadedAsync();
        }
        catch
        {
            // 拿不到字典就保持占位文字，不影响正文本身
        }

        _catalogReady = EmoteCatalog.Shared.IsLoaded;

        // 控件可能已经被回收（列表虚拟化），再画一次是安全的，但没必要
        if (_catalogReady && IsLoaded) Render();
    }

    private void Render()
    {
        if (!_catalogReady && EmoteCatalog.Shared.IsLoaded) _catalogReady = true;

        var paragraph = new Paragraph();
        var segments = UbbParser.Parse(Text);
        double fontSize = FontSize > 0 ? FontSize : 13;

        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case UbbSegmentKind.Text:
                    paragraph.Inlines.Add(new Run
                    {
                        Text = segment.Text,
                        FontSize = fontSize
                    });
                    break;

                case UbbSegmentKind.LineBreak:
                    paragraph.Inlines.Add(new LineBreak());
                    break;

                case UbbSegmentKind.Emote when !string.IsNullOrWhiteSpace(segment.Url):
                    paragraph.Inlines.Add(BuildEmoteInline(segment.Url!, fontSize, segment.Name));
                    break;

                case UbbSegmentKind.Image when !string.IsNullOrWhiteSpace(segment.Url):
                    paragraph.Inlines.Add(BuildImageInline(segment.Url!));
                    break;
            }
        }

        // 除表情/图片外没有任何文字时，给个空段落，避免 RichTextBlock 高度塌成 0
        if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new Run { Text = string.Empty });

        Root.Blocks.Clear();
        Root.Blocks.Add(paragraph);
    }

    /// <summary>
    /// 表情：高度跟字号走（约 1.35 倍），底部对齐基线上方一点。
    /// 图片地址是没扩展名的 CDN 地址，BitmapImage 靠 Content-Type 识别，能正常解码。
    /// </summary>
    private InlineUIContainer BuildEmoteInline(string url, double fontSize, string? name)
    {
        double edge = Math.Round(fontSize * 1.5);

        var image = new Image
        {
            Source = new BitmapImage(new Uri(url)),
            Width = edge,
            Height = edge,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        if (!string.IsNullOrWhiteSpace(name)) ToolTipService.SetToolTip(image, name);

        return new InlineUIContainer { Child = image };
    }

    /// <summary>图片评论：限宽限高，避免一张大图把评论列表撑爆</summary>
    private InlineUIContainer BuildImageInline(string url)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri(url)),
            MaxWidth = 280,
            MaxHeight = 200,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 0, 4)
        };

        return new InlineUIContainer { Child = image };
    }
}
