using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AStation_Client.Models;
using AStation_Client.Services;

namespace AStation_Client.Pages;

public sealed partial class LivePage : Page
{
    public ObservableCollection<LiveItem> Items { get; } = new();

    private readonly List<ToggleButton> _chips = new();
    private int _currentFilterId;
    private bool _categoriesBuilt;

    public LivePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadDataAsync();
    }

    /// <summary>
    /// 走 live.acfun.cn/api/channel/list（带 filters 分类参数）。
    /// filterId：0 全部 / 1 游戏 / 2 其他 / 3 娱乐 / 4 虚拟偶像。
    /// 接口不可用时回退到首页 SSR 的 liveList，保证至少能看到热门房间。
    /// </summary>
    private async Task LoadDataAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;

        LiveChannelResult? result = null;
        try
        {
            result = await ServiceLocator.Live.GetChannelListAsync(_currentFilterId);
        }
        catch
        {
            // 网络异常：走下面的兜底
        }

        if (!_categoriesBuilt && result is not null && result.Categories.Count > 0)
        {
            BuildCategoryChips(result.Categories);
            _categoriesBuilt = true;
        }

        List<LiveItem> items = result?.Items ?? new List<LiveItem>();

        // 兜底：分类接口整体不可用时，读直播站首页 SSR
        if (items.Count == 0 && _currentFilterId == 0)
        {
            try
            {
                items = await ServiceLocator.Live.GetHotLiveListAsync();
            }
            catch
            {
                // 保持为空
            }
        }

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        LiveList.ItemsSource = Items;

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;

        int total = result?.TotalCount ?? 0;
        bool empty = Items.Count == 0;

        SectionTitle.Text = _currentFilterId == 0
            ? "正在直播"
            : $"正在直播 · {CurrentCategoryName()}";

        if (empty)
        {
            HintText.Text = _currentFilterId == 0
                ? "未能获取直播列表（直播站页面结构可能已变更，或网络不可达）"
                : "该分类当前没有主播在线，换个分类看看";
        }
        else
        {
            HintText.Text = total > Items.Count
                ? $"共 {total} 个在播房间，已加载 {Items.Count} 个"
                : $"共 {Items.Count} 个正在直播的房间";
        }
        HintText.Visibility = Visibility.Visible;
    }

    private string CurrentCategoryName()
    {
        foreach (var chip in _chips)
        {
            if (chip.Tag is int id && id == _currentFilterId) return chip.Content?.ToString() ?? "全部";
        }
        return "全部";
    }

    /// <summary>
    /// 用接口返回的真实分类标签构建筛选芯片。
    /// </summary>
    private void BuildCategoryChips(List<LiveCategory> categories)
    {
        CategoryPanel.Children.Clear();
        _chips.Clear();

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.Name)) continue;

            var chip = new ToggleButton
            {
                Content = category.Name,
                Tag = category.FilterId,
                IsChecked = category.FilterId == _currentFilterId,
                MinWidth = 0,
                Padding = new Thickness(16, 6, 16, 6),
                CornerRadius = new CornerRadius(16),
                FontSize = 13
            };
            chip.Click += CategoryChip_Click;

            _chips.Add(chip);
            CategoryPanel.Children.Add(chip);
        }
    }

    private async void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        if (clicked.Tag is not int filterId) return;

        // 手动互斥：单选语义，点击已选中的芯片视为保持原样
        foreach (var chip in _chips)
        {
            chip.IsChecked = ReferenceEquals(chip, clicked);
        }

        if (filterId == _currentFilterId) return;
        _currentFilterId = filterId;

        await LoadDataAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    /// <summary>
    /// 点击直播卡片 → 进入直播间播放页
    /// </summary>
    private void LiveCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        // ItemsRepeater + x:Bind 模板的根元素没有 DataContext，数据对象走 Tag
        var item = element.Tag as LiveItem ?? element.DataContext as LiveItem;

        if (item is null)
        {
            RuntimeLog.Write("live", "卡片点击：取不到 LiveItem（Tag / DataContext 都为空）");
            return;
        }

        if (string.IsNullOrWhiteSpace(item.AuthorId) || string.IsNullOrWhiteSpace(item.LiveId))
        {
            RuntimeLog.Write("live", $"卡片点击：缺少拉流参数 authorId='{item.AuthorId}' liveId='{item.LiveId}'");
            return;
        }

        RuntimeLog.Write("live", $"进入直播间 {item.Author} / {item.Title}（authorId={item.AuthorId} liveId={item.LiveId}）");

        Frame.Navigate(typeof(LiveRoomPage), new LiveNavArgs
        {
            AuthorId = item.AuthorId,
            LiveId = item.LiveId,
            Title = item.Title,
            Author = item.Author,
            AuthorAvatar = item.Avatar,
            Category = item.Tag,
            OnlineText = item.OnlineText,
            Cover = item.Cover
        });
    }
}
