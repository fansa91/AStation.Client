using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AStation_Client.Controls;
using AStation_Client.Models;
using AStation_Client.Services;

namespace AStation_Client.Pages.Overlay;

public sealed partial class SearchPage : Page
{
    /// <summary>最近搜索最多留 8 条（输入框预览全部，空态胶囊只放前 4 条，避免挤成两行）</summary>
    private const int HistoryLimit = 8;
    private const int HistoryChipLimit = 4;

    public ObservableCollection<VideoItem> Results { get; } = new();

    private readonly List<string> _history = new();

    private string _keyword = string.Empty;
    private SearchSort _sort = SearchSort.Relevance;
    private long _total;

    private bool _loading;
    private bool _searched;
    private bool _loadFailed;

    public SearchPage()
    {
        InitializeComponent();
        ResultList.ItemsSource = Results;
        Loaded += (_, _) => SearchInput.Focus(FocusState.Programmatic);
    }

    /// <summary>参数是标题栏搜索框带来的关键词，直接开搜</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        LoadHistory();

        if (e.Parameter is string keyword && !string.IsNullOrWhiteSpace(keyword))
        {
            SearchInput.Text = keyword;
            _ = SearchAsync(keyword.Trim());
        }
        else
        {
            ShowInitial();
        }
    }

    // ───────────────────────────── 搜索 ─────────────────────────────

    private async void SearchInput_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // 点建议项时 ChosenSuggestion 是选中的历史词，回车时只有 QueryText
        string keyword = (args.ChosenSuggestion as string
            ?? args.QueryText
            ?? sender.Text
            ?? string.Empty).Trim();
        if (keyword.Length == 0) return;

        SearchInput.Text = keyword;
        await SearchAsync(keyword);
    }

    private void SearchInput_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 程序回填（导航参数 / 点建议）不参与过滤，否则会把建议列表又弹一遍
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        string text = (sender.Text ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            sender.ItemsSource = null;
            if (_searched) ShowInitial();
            return;
        }

        // 用最近搜索当联想词；命中才给列表，没命中就不弹
        var matches = _history
            .Where(h => h.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(HistoryLimit)
            .ToList();
        sender.ItemsSource = matches.Count > 0 ? matches : null;
    }

    private async Task SearchAsync(string keyword)
    {
        _keyword = keyword;
        _loadFailed = false;
        _searched = true;

        Results.Clear();
        EmptyPanel.Visibility = Visibility.Collapsed;
        SortBar.Visibility = Visibility.Visible;
        ResultCountText.Text = string.Empty;
        ResultScroll.ChangeView(null, 0, null, disableAnimation: true);

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading || string.IsNullOrWhiteSpace(_keyword)) return;
        _loading = true;

        // 加载中别让人连点排序，否则几个请求会互相覆盖
        SortBar.IsHitTestVisible = false;
        SearchState.ShowLoading($"正在搜索「{_keyword}」…");
        _loadFailed = false;

        try
        {
            var result = await ServiceLocator.Search.SearchVideosAsync(_keyword, _sort);
            _total = result.Total;

            foreach (var item in result.Items)
            {
                Results.Add(item);
            }

            if (Results.Count > 0)
            {
                SearchState.Hide();
                RememberSearch(_keyword);
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            App.WriteLog("SearchPage.LoadAsync", ex);
            SearchState.ShowError($"搜索失败：{ex.Message}", actionText: "重试");
        }
        finally
        {
            _loading = false;
            SortBar.IsHitTestVisible = true;
            UpdateResultSummary();
        }
    }

    private void UpdateResultSummary()
    {
        if (Results.Count == 0)
        {
            ResultCountText.Text = string.Empty;

            // 失败态已经由 ShowError 呈现，这里不能再盖成「没有找到」
            if (!_loadFailed && !_loading)
            {
                SearchState.ShowEmpty(
                    "没有找到相关视频",
                    "换个关键词试试，或换一种排序。",
                    glyph: "\uE721");
            }
            return;
        }

        // 接口只给前 30 条，命中更多时如实说明，别让人以为还有下一页
        ResultCountText.Text = _total > Results.Count
            ? $"共 {_total} 条 · 显示前 {Results.Count} 条"
            : $"共 {Results.Count} 条";
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var sort = ReferenceEquals(sender, SortNewest) ? SearchSort.Newest
            : ReferenceEquals(sender, SortMostViewed) ? SearchSort.MostViewed
            : SearchSort.Relevance;

        // 点已选中的那个会把勾去掉，先把勾选态复位再说
        if (sort == _sort)
        {
            ApplySortChecks(sort);
            return;
        }

        _sort = sort;
        ApplySortChecks(sort);

        if (_searched && !string.IsNullOrWhiteSpace(_keyword)) _ = SearchAsync(_keyword);
    }

    private void ApplySortChecks(SearchSort sort)
    {
        SortRelevance.IsChecked = sort == SearchSort.Relevance;
        SortMostViewed.IsChecked = sort == SearchSort.MostViewed;
        SortNewest.IsChecked = sort == SearchSort.Newest;
    }

    /// <summary>回到初始态：清空结果、收起排序行，露出最近搜索</summary>
    private void ShowInitial()
    {
        Results.Clear();
        _searched = false;
        _keyword = string.Empty;
        _total = 0;

        SortBar.Visibility = Visibility.Collapsed;
        SearchState.Hide();
        ResultCountText.Text = string.Empty;

        RefreshHistoryChips();
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private async void SearchState_ActionClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_keyword)) return;
        await LoadAsync();
    }

    /// <summary>点结果卡片：关掉覆盖层，在主框架打开播放页</summary>
    private void ResultCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaCard { Payload: string id } || string.IsNullOrWhiteSpace(id)) return;

        if (App.MainWindowInstance is MainWindow window)
        {
            window.OpenMainPageFromOverlay(typeof(VideoPage), id);
        }
    }

    // ───────────────────────────── 最近搜索 ─────────────────────────────

    private void LoadHistory()
    {
        _history.Clear();
        foreach (var item in AppSettings.Current.SearchHistory)
        {
            if (!string.IsNullOrWhiteSpace(item)) _history.Add(item);
        }
    }

    private void RememberSearch(string keyword)
    {
        var stored = AppSettings.Current.SearchHistory;

        stored.RemoveAll(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase));
        stored.Insert(0, keyword);
        if (stored.Count > HistoryLimit) stored.RemoveRange(HistoryLimit, stored.Count - HistoryLimit);
        AppSettings.Current.Save();

        LoadHistory();
        RefreshHistoryChips();
    }

    private void RefreshHistoryChips()
    {
        HistoryChips.ItemsSource = _history.Take(HistoryChipLimit).ToList();
        HistorySection.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string keyword } || string.IsNullOrWhiteSpace(keyword)) return;

        SearchInput.Text = keyword;
        _ = SearchAsync(keyword);
    }
}
