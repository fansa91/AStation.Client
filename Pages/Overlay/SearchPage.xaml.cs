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
    // 服务端一页固定下发 30 条，size 传 20 也按 30 返回，所以这里对齐成 30，翻页判断才不会失效
    private const int PageSize = 30;

    public ObservableCollection<VideoItem> Results { get; } = new();

    private string _keyword = string.Empty;
    private int _page = 1;
    private long _total;
    private bool _loading;
    private bool _reachedEnd;
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

        if (e.Parameter is string keyword && !string.IsNullOrWhiteSpace(keyword))
        {
            SearchInput.Text = keyword;
            _ = SearchAsync(keyword.Trim());
        }
        else
        {
            SearchState.ShowEmpty("搜点什么", "输入关键词后回车，结果会以卡片列表显示。", glyph: "\uE721");
        }
    }

    private async void SearchInput_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string keyword = (args.QueryText ?? sender.Text ?? string.Empty).Trim();
        if (keyword.Length == 0) return;

        await SearchAsync(keyword);
    }

    /// <summary>清空输入时回到初始提示，避免残留上一次的结果条数说明</summary>
    private void SearchInput_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        if (!string.IsNullOrWhiteSpace(sender.Text)) return;

        _keyword = string.Empty;
        _reachedEnd = false;
        Results.Clear();
        HintText.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        SearchState.ShowEmpty("搜点什么", "输入关键词后回车，结果会以卡片列表显示。", glyph: "\uE721");
    }

    private async Task SearchAsync(string keyword)
    {
        _keyword = keyword;
        _page = 1;
        _reachedEnd = false;
        _loadFailed = false;
        Results.Clear();
        LoadMoreButton.Visibility = Visibility.Collapsed;

        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (_loading || string.IsNullOrWhiteSpace(_keyword)) return;
        _loading = true;

        SearchState.ShowLoading($"正在搜索「{_keyword}」…");
        _loadFailed = false;

        try
        {
            var result = await ServiceLocator.Search.SearchVideosAsync(_keyword, _page, PageSize);
            _total = result.Total;

            foreach (var item in result.Items)
            {
                Results.Add(item);
            }

            _reachedEnd = result.Items.Count == 0
                || (_total > 0 && Results.Count >= _total)
                || result.Items.Count < PageSize;
            if (Results.Count > 0) SearchState.Hide();
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            App.WriteLog("SearchPage.LoadPage", ex);

            if (Results.Count == 0)
            {
                SearchState.ShowError($"搜索失败：{ex.Message}", actionText: "重试");
            }
            else
            {
                HintText.Visibility = Visibility.Visible;
                HintText.Text = $"已显示 {Results.Count} 条，继续加载时出错：{ex.Message}";
            }
        }
        finally
        {
            _loading = false;
            UpdateHint();
        }
    }

    private void UpdateHint()
    {
        if (Results.Count == 0)
        {
            // 失败态已经由 SearchState.ShowError 呈现，这里不能再盖成「没有找到」
            if (!_loadFailed && !_loading && _page == 1)
            {
                SearchState.ShowEmpty(
                    "没有找到相关视频",
                    "换个关键词试试，或检查网络后重试。",
                    glyph: "\uE721");
            }

            HintText.Visibility = Visibility.Collapsed;
            LoadMoreButton.Visibility = Visibility.Collapsed;
            return;
        }

        HintText.Visibility = Visibility.Visible;
        HintText.Text = _total > 0
            ? $"「{_keyword}」共 {_total} 条结果，已显示 {Results.Count} 条"
            : $"「{_keyword}」已显示 {Results.Count} 条";

        LoadMoreButton.Visibility = _reachedEnd ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreButton.IsEnabled = !_loading;
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _reachedEnd) return;
        _page++;
        await LoadPageAsync();
    }

    private async void SearchState_ActionClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_keyword)) return;
        _page = 1;
        _reachedEnd = false;
        Results.Clear();
        await LoadPageAsync();
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
}
