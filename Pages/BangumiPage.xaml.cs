using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AStation_Client.Controls;
using AStation_Client.Models;
using AStation_Client.Services;

namespace AStation_Client.Pages;

public sealed partial class BangumiPage : Page
{
    public ObservableCollection<BangumiItem> Items { get; } = new();

    /// <summary>true = 我的追番（需登录），false = 番剧库（/bangumilist，免登录）</summary>
    private bool _showFavorites;

    public BangumiPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadDataAsync();
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
        BangumiState.Hide();

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
        BangumiScroll.Visibility = BangumiList.Visibility;
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
            BangumiState.Hide();
            return;
        }

        HintText.Visibility = Visibility.Collapsed;

        if (_showFavorites)
        {
            BangumiState.ShowEmpty(
                "还没有追番数据",
                "「我的追番」读取的是你账号下的收藏，需要登录；未登录时可以先看番剧库。",
                actionText: "去登录");
        }
        else
        {
            BangumiState.ShowEmpty(
                "番剧库暂时取不到数据",
                "A 站番剧页（/bangumilist）可能临时不可用，或网络不可达。稍后刷新试试，或先看「我的追番」。");
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
    private void BangumiCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaCard { Payload: BangumiItem item }) return;
        if (string.IsNullOrWhiteSpace(item.BangumiId)) return;

        Frame.Navigate(typeof(VideoPage), new VideoNavArgs
        {
            ResourceId = item.BangumiId,
            ResourceType = 1,
            Title = item.Title
        });
    }

    private void BangumiState_ActionClick(object? sender, EventArgs e)
    {
        if (App.MainWindowInstance is MainWindow mainWindow)
        {
            mainWindow.OpenOverlay(typeof(LoginPage));
        }
    }
}
