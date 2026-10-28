using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AStation_Client.Models;
using AStation_Client.Services;

namespace AStation_Client.Pages;

public sealed partial class BangumiPage : Page
{
    public ObservableCollection<BangumiItem> Items { get; } = new();

    /// <summary>卡片最小宽度（含间距之外的净宽）</summary>
    private const double MinCardWidth = 150;

    /// <summary>卡片纵向间距</summary>
    private const double CardGap = 16;

    /// <summary>封面高度（竖版 3:4 附近的固定裁切高度）</summary>
    private const double CoverHeight = 200;

    /// <summary>true = 我的追番（需登录），false = 番剧库（/bangumilist，免登录）</summary>
    private bool _showFavorites;

    /// <summary>面板未就绪时的重试次数，避免无限排队</summary>
    private int _adjustRetry;

    public BangumiPage()
    {
        InitializeComponent();
        SizeChanged += BangumiPage_SizeChanged;
        Loaded += BangumiPage_Loaded;
    }

    private void BangumiPage_Loaded(object sender, RoutedEventArgs e)
    {
        AdjustCardSize();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadDataAsync();
        AdjustCardSize();
    }

    /// <summary>
    /// 按可用宽度重算列数与卡片宽度，让网格始终填满并且每张卡等宽。
    /// ItemsWrapGrid 的 ItemWidth 是包含间距的单元格宽度。
    /// </summary>
    private void AdjustCardSize()
    {
        if (BangumiList.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            // 面板还没生成（首次导航时常见）：下一帧再试，最多重试 5 次
            if (_adjustRetry++ < 5)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AdjustCardSize);
            }
            return;
        }

        _adjustRetry = 0;

        double available = BangumiList.ActualWidth;
        if (available <= 0) return;

        // 单元格宽度 = 卡片宽 + 右侧间距；列数四舍五入，尽量填满一行而不溢出
        int columns = Math.Max(2, (int)Math.Round(available / (MinCardWidth + CardGap)));
        double cell = available / columns;

        panel.ItemWidth = cell;

        // 封面 + 标题两行 + 上下间距
        panel.ItemHeight = CoverHeight + 8 + 44 + CardGap;
    }

    private void BangumiPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AdjustCardSize();
    }

    /// <summary>
    /// 番剧库：解析 /bangumilist 的 SSR HTML（公开可用的真实数据，29 部）。
    /// 我的追番：/rest/pc-direct/feed/favorite/bangumi（需登录态）。
    /// 两者都不做假数据兜底，空就明确说明原因。
    /// </summary>
    private async Task LoadDataAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;

        List<BangumiItem> items = new();
        try
        {
            items = _showFavorites
                ? await ServiceLocator.Bangumi.GetFavoriteBangumiAsync()
                : await ServiceLocator.Bangumi.GetBangumiListAsync();
        }
        catch
        {
            // 网络异常：按空处理
        }

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        BangumiList.ItemsSource = Items;

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;

        UpdateEmptyState(items.Count);

        BangumiList.Visibility = Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateEmptyState(int count)
    {
        bool empty = count == 0;

        if (!empty)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = _showFavorites
                ? $"我的追番 · {count} 部"
                : $"番剧库 · {count} 部（按追番人数排序，数据来自 A 站番剧页）";
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        HintText.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;

        if (_showFavorites)
        {
            EmptyTitle.Text = "还没有追番数据";
            EmptyDescription.Text = "「我的追番」读取的是你账号下的收藏，需要登录；未登录时可以先看番剧库。";
            GoLoginButton.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyTitle.Text = "番剧库暂时取不到数据";
            EmptyDescription.Text = "A 站番剧页（/bangumilist）可能临时不可用，或网络不可达。稍后刷新试试，或先看「我的追番」。";
            GoLoginButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void ScopeChip_Click(object sender, RoutedEventArgs e)
    {
        bool toFavorites = ReferenceEquals(sender, FavoriteChip);

        LibraryChip.IsChecked = !toFavorites;
        FavoriteChip.IsChecked = toFavorites;

        if (_showFavorites == toFavorites) return;
        _showFavorites = toFavorites;

        await LoadDataAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    /// <summary>
    /// 番剧取流用 resourceType=1，resourceId 传 bangumiId（不是 itemId）。
    /// </summary>
    private void BangumiList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BangumiItem item) return;
        if (string.IsNullOrWhiteSpace(item.BangumiId)) return;

        Frame.Navigate(typeof(VideoPage), new VideoNavArgs
        {
            ResourceId = item.BangumiId,
            ResourceType = 1,
            Title = item.Title
        });
    }

    private void GoLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance is MainWindow mainWindow)
        {
            mainWindow.OpenOverlay(typeof(LoginPage));
        }
    }
}
