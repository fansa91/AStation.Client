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

public sealed partial class RankPage : Page
{
    public ObservableCollection<RankEntry> Items { get; } = new();

    private readonly List<ToggleButton> _periodChips = new();
    private readonly List<ToggleButton> _channelChips = new();

    private RankPeriod _currentPeriod = RankPeriod.Today;
    private int _currentChannelId;
    private string _currentChannelName = "全站";
    private bool _channelsBuilt;

    public RankPage()
    {
        InitializeComponent();
        BuildPeriodChips();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 首页 / 频道页的「分区」入口带 channelId 进来，直接落到该分区榜单
        if (e.Parameter is int channelId && channelId > 0)
        {
            _currentChannelId = channelId;
        }

        await LoadDataAsync();
    }

    /// <summary>
    /// 周期筛选芯片：日榜 / 三日榜 / 周榜
    /// </summary>
    private void BuildPeriodChips()
    {
        foreach (var period in new[] { RankPeriod.Today, RankPeriod.ThreeDays, RankPeriod.Week })
        {
            var chip = CreateChip(RankService.ToPeriodLabel(period), period);
            chip.IsChecked = period == _currentPeriod;
            chip.Click += PeriodChip_Click;

            _periodChips.Add(chip);
            PeriodPanel.Children.Add(chip);
        }
    }

    /// <summary>
    /// 频道筛选芯片：全站 + 各一级频道（cid 即接口的 channelId）
    /// </summary>
    private void BuildChannelChips(List<RankChannel> channels)
    {
        ChannelPanel.Children.Clear();
        _channelChips.Clear();

        foreach (var channel in channels)
        {
            var chip = CreateChip(channel.Name, channel.ChannelId);
            chip.IsChecked = channel.ChannelId == _currentChannelId;
            chip.Click += ChannelChip_Click;

            // 从外部带 channelId 进来时，标题要显示对应的分区名而不是「全站」
            if (channel.ChannelId == _currentChannelId)
            {
                _currentChannelName = channel.Name;
            }

            _channelChips.Add(chip);
            ChannelPanel.Children.Add(chip);
        }
    }

    private static ToggleButton CreateChip(string text, object tag) => new()
    {
        Content = text,
        Tag = tag,
        MinWidth = 0,
        Padding = new Thickness(16, 6, 16, 6),
        CornerRadius = new CornerRadius(16),
        FontSize = 13
    };

    private async void PeriodChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not RankPeriod period) return;

        foreach (var chip in _periodChips)
        {
            chip.IsChecked = ReferenceEquals(chip, clicked);
        }

        if (period == _currentPeriod) return;
        _currentPeriod = period;

        await LoadDataAsync();
    }

    private async void ChannelChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not int channelId) return;

        foreach (var chip in _channelChips)
        {
            chip.IsChecked = ReferenceEquals(chip, clicked);
        }

        if (channelId == _currentChannelId) return;

        _currentChannelId = channelId;
        _currentChannelName = clicked.Content?.ToString() ?? "全站";

        await LoadDataAsync();
    }

    /// <summary>
    /// GET /rest/pc-direct/rank/channel
    /// 参数：channelId（0=全站）、rankLimit、rankPeriod（DAY / THREE_DAYS / WEEK）
    /// </summary>
    private async Task LoadDataAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;

        if (Items.Count == 0)
        {
            RankState.ShowLoading("正在加载榜单…");
        }

        if (!_channelsBuilt)
        {
            try
            {
                var channels = await ServiceLocator.Rank.GetRankChannelsAsync();
                if (channels.Count > 1)
                {
                    BuildChannelChips(channels);
                    _channelsBuilt = true;
                }
            }
            catch
            {
                // 拿不到频道列表就只保留「全站」（下面的兜底芯片）
            }

            if (!_channelsBuilt)
            {
                BuildChannelChips(new List<RankChannel> { new() { ChannelId = 0, Name = "全站" } });
            }
        }

        List<RankEntry> entries = new();
        string? error = null;
        try
        {
            entries = await ServiceLocator.Rank.GetRankListAsync(_currentChannelId, _currentPeriod);
        }
        catch (Exception ex)
        {
            // 网络异常：不再静默当空，交给状态面板说明并可重试
            error = ex.Message;
        }

        Items.Clear();
        foreach (var entry in entries)
        {
            Items.Add(entry);
        }

        RankList.ItemsSource = Items;

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;

        SectionTitle.Text = $"{_currentChannelName} · {RankService.ToPeriodLabel(_currentPeriod)}";

        bool empty = Items.Count == 0;

        if (error is not null)
        {
            RankState.ShowError($"榜单加载失败：{error}", actionText: "重试");
            HintText.Visibility = Visibility.Collapsed;
            return;
        }

        if (empty)
        {
            RankState.ShowEmpty(
                $"{_currentChannelName}的{PerioShortLabel()}暂时没有数据",
                "换个周期或频道试试。",
                actionText: "重试");
            HintText.Visibility = Visibility.Collapsed;
            return;
        }

        RankState.Hide();
        HintText.Text = $"共 {Items.Count} 条 · 点击任意条目直接播放";
        HintText.Visibility = Visibility.Visible;
    }

    private async void RankState_ActionClick(object? sender, EventArgs e)
    {
        await LoadDataAsync();
    }

    private string PerioShortLabel() => _currentPeriod switch
    {
        RankPeriod.ThreeDays => "三日榜",
        RankPeriod.Week => "周榜",
        _ => "日榜"
    };

    private void RankList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RankEntry entry) return;
        if (string.IsNullOrWhiteSpace(entry.ResourceId)) return;

        Frame.Navigate(typeof(VideoPage), entry.ResourceId);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }
}
