using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AStation_Client.Controls;
using AStation_Client.ViewModels;

namespace AStation_Client.Pages;

public sealed partial class HomePage : Page
{
    /// <summary>窄屏把 Hero 右侧三条收起来，只留榜一大卡</summary>
    private const double BreakpointHeroNarrow = 900;

    private const double HeroSideWidth = 300;
    private const double HeroColumnGap = 16;

    private bool _loadedOnce;
    private double _lastPageWidth = -1;

    public HomePage()
    {
        InitializeComponent();

        SizeChanged += (_, e) => UpdateResponsiveLayout(e.NewSize.Width);

        // Hero 高度依赖自己的实际宽度：窗口尺寸变化后可能只差几十毫秒才布局完成，
        // 再挂一次容器自身的 SizeChanged 兜底，避免用到启动瞬间的窄宽度
        HeroGrid.SizeChanged += (_, _) => UpdateHeroHeight();

        Loaded += (_, _) => UpdateResponsiveLayout(ActualWidth);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 从播放页返回首页不重新拉数据，保留原列表与滚动位置
        if (_loadedOnce) return;
        _loadedOnce = true;

        await LoadHomeAsync();
    }

    private async Task LoadHomeAsync()
    {
        HomeState.ShowLoading("正在加载首页…");
        await ViewModel.LoadDataAsync();
        UpdateState();
    }

    private void UpdateState()
    {
        if (!string.IsNullOrEmpty(ViewModel.ErrorMessage))
        {
            HomeState.ShowError(ViewModel.ErrorMessage, actionText: "重试");
        }
        else
        {
            HomeState.Hide();
        }
    }

    /// <summary>
    /// 断点只按宽度判断，且做去重：Hero 高度变化会再次触发 SizeChanged，
    /// 不去重就会来回自激（宽度没变就没必要重算）。
    /// </summary>
    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0) return;

        if (Math.Abs(_lastPageWidth - width) >= 1)
        {
            _lastPageWidth = width;

            bool narrow = width < BreakpointHeroNarrow;
            ContentPanel.Padding = narrow
                ? new Thickness(16, 16, 16, 20)
                : new Thickness(24, 20, 24, 24);

            HeroSideColumn.Width = narrow ? new GridLength(0) : new GridLength(HeroSideWidth);
            HeroSide.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        }

        UpdateHeroHeight();
    }

    /// <summary>
    /// Hero 高度必须自己算：大卡是「封面铺满」的网格，没有内在高度，
    /// 让它按 16:9 跟宽度走，右侧三条再靠 * 行三等分对齐。
    /// </summary>
    private void UpdateHeroHeight()
    {
        double total = HeroGrid.ActualWidth;
        if (total <= 0) return;

        double sideWidth = HeroSide.Visibility == Visibility.Visible ? HeroSideWidth + HeroColumnGap : 0;
        double heroWidth = total - sideWidth;
        if (heroWidth <= 0) return;

        double heroHeight = Math.Clamp(Math.Round(heroWidth * 9.0 / 16.0), 180, 380);

        // Height 初始是 NaN（Auto）：NaN 参与比较恒为 false，必须先单独判掉，
        // 否则高度永远停在「由右侧小条撑出来的自然高度」
        if (double.IsNaN(HeroMain.Height) || Math.Abs(HeroMain.Height - heroHeight) >= 1)
        {
            HeroMain.Height = heroHeight;
        }
    }

    // ───────────────────────────── 交互 ─────────────────────────────

    private void Hero_Click(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && !string.IsNullOrWhiteSpace(id))
        {
            Frame.Navigate(typeof(VideoPage), id);
        }
    }

    private void VideoCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MediaCard { Payload: string id } && !string.IsNullOrWhiteSpace(id))
        {
            Frame.Navigate(typeof(VideoPage), id);
        }
    }

    /// <summary>分区胶囊 → 该分区的排行榜（cid 与排行榜接口的 channelId 同源）</summary>
    private void ChannelChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int channelId } && channelId > 0)
        {
            Frame.Navigate(typeof(RankPage), channelId);
        }
    }

    private void AllChannels_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ChannelPage));
    }

    private async void RefreshVideos_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshRecommendAsync();
        UpdateState();
    }

    private async void HomeState_ActionClick(object? sender, EventArgs e)
    {
        await LoadHomeAsync();
    }
}
