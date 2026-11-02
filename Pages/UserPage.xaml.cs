using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using AStation_Client.Services;

namespace AStation_Client.Pages;

/// <summary>
/// 我的空间：账号卡 + 播放历史 / 收藏两个分页。
///
/// 这里是**主框架里的跳转页**（不是覆盖层）：从标题栏账号按钮或侧栏账号项进来，
/// 吃后退栈，随时可以回上一页。
///
/// 播放历史走服务端接口（需登录）；收藏在网页端没有公开接口，先给明确空态。
/// </summary>
public sealed partial class UserPage : Page
{
    private const int PageSize = 20;

    private int _pageNo;
    private int _totalCount;
    private bool _loading;
    private string? _loadedForUser;
    private int _tabIndex;

    public ObservableCollection<HistoryItem> History { get; } = new();

    public UserPage()
    {
        InitializeComponent();

        // 从历史点进视频再返回时，列表和滚动位置都要留住
        NavigationCacheMode = NavigationCacheMode.Enabled;

        HistoryList.ItemsSource = History;
        ApplyLoggedInState();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ApplyLoggedInState();

        // 换了账号（或从没登录变成登录）才重新拉，单纯回退不重刷
        if (AccountState.IsLoggedIn && _loadedForUser != AccountState.UserName)
        {
            _ = LoadHistoryAsync(reset: true);
        }

    }

    // ───────────────────────── 账号区 ─────────────────────────

    private void ApplyLoggedInState()
    {
        bool loggedIn = AccountState.IsLoggedIn;

        UserNameText.Text = loggedIn
            ? (string.IsNullOrWhiteSpace(AccountState.UserName) ? "已登录用户" : AccountState.UserName!)
            : "未登录";

        UserMetaText.Text = loggedIn
            ? BuildMetaText()
            : "登录后可以查看播放历史与收藏";

        LoginButton.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

        Avatar.DisplayName = AccountState.UserName ?? string.Empty;
        if (loggedIn
            && !string.IsNullOrWhiteSpace(AccountState.AvatarUrl)
            && Uri.TryCreate(AccountState.AvatarUrl, UriKind.Absolute, out var uri))
        {
            Avatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        }
        else
        {
            Avatar.ProfilePicture = null;
        }

        if (!loggedIn && _tabIndex == 0)
        {
            ShowHistoryEmpty();
        }
    }

    private static string BuildMetaText()
    {
        var parts = new List<string>();
        if (AccountState.UserId > 0) parts.Add($"uid {AccountState.UserId}");
        parts.Add("播放历史与账号数据来自 AcFun");
        return string.Join(" · ", parts);
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindowInstance?.OpenOverlay(typeof(LoginPage));
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        SessionStore.Clear(ServiceLocator.Api);
        AccountState.Clear();

        History.Clear();
        _loadedForUser = null;
        _pageNo = 0;
        _totalCount = 0;
        CountText.Text = string.Empty;
        LoadMoreButton.Visibility = Visibility.Collapsed;

        ApplyLoggedInState();
        SelectTab(0, force: true);
    }

    // ───────────────────────── 分页 ─────────────────────────

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as ToggleButton;
        int index = ReferenceEquals(button, CollectTab) ? 1 : 0;

        // 点当前分页时 ToggleButton 会先取消勾选，补回来直接返回，否则内容会闪一下
        if (index == _tabIndex)
        {
            if (button is not null) button.IsChecked = true;
            return;
        }

        SelectTab(index);
    }

    private void SelectTab(int index, bool force = false)
    {
        if (!force && index == _tabIndex) return;

        _tabIndex = index;
        bool history = index == 0;

        HistoryTab.IsChecked = history;
        CollectTab.IsChecked = !history;

        HistoryPanel.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        CollectState.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryState.Visibility = history && HistoryState.IsShowing
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (history)
        {
            CountText.Text = _totalCount > 0 ? $"共 {_totalCount} 条" : string.Empty;
            if (!AccountState.IsLoggedIn) ShowHistoryEmpty();
        }
        else
        {
            CountText.Text = string.Empty;
            ShowCollectState();
        }
    }

    private void ShowCollectState()
    {
        if (!AccountState.IsLoggedIn)
        {
            CollectState.ShowEmpty(
                "登录后才能看收藏",
                "AcFun 的收藏夹跟账号绑定，登录之后再来。",
                actionText: "去登录",
                glyph: "\uE77B");
            return;
        }

        CollectState.ShowEmpty(
            "收藏夹还没接入",
            "AcFun 网页端的收藏列表接口没有公开文档，试过的候选路径都是 404；确认接口后这里会列出你的收藏夹。",
            actionText: null,
            glyph: "\uE734");
    }

    private void CollectState_ActionClick(object? sender, EventArgs e)
    {
        App.MainWindowInstance?.OpenOverlay(typeof(LoginPage));
    }

    // ───────────────────────── 播放历史 ─────────────────────────

    private void ShowHistoryEmpty()
    {
        HistoryPanel.Visibility = Visibility.Collapsed;
        CountText.Text = string.Empty;
        HistoryState.Visibility = Visibility.Visible;
        HistoryState.ShowEmpty(
            "还没有登录",
            "登录后这里会显示你看过的视频，换台机器也能接着看。",
            actionText: "去登录",
            glyph: "\uE77B");
    }

    private async Task LoadHistoryAsync(bool reset)
    {
        if (_loading) return;
        if (!AccountState.IsLoggedIn)
        {
            ShowHistoryEmpty();
            return;
        }

        _loading = true;

        if (reset)
        {
            _pageNo = 0;
            _totalCount = 0;
            History.Clear();
            HistoryPanel.Visibility = Visibility.Collapsed;
            HistoryState.Visibility = Visibility.Visible;
            HistoryState.ShowLoading("正在读取播放历史…");
        }
        else
        {
            LoadMoreButton.Visibility = Visibility.Collapsed;
            MoreRing.Visibility = Visibility.Visible;
            MoreRing.IsActive = true;
        }

        try
        {
            var page = await ServiceLocator.History.GetHistoryAsync(_pageNo + 1, PageSize);

            if (page.Result == -401)
            {
                // 会话过期：标记与 Cookie 一起撤掉，页面回到未登录态
                SessionStore.Clear(ServiceLocator.Api);
                AccountState.Clear();
                ApplyLoggedInState();
                ShowHistoryEmpty();
                return;
            }

            if (page.Result != 0)
            {
                ShowHistoryError(page.ErrorMessage);
                return;
            }

            _pageNo++;
            _totalCount = page.TotalCount;
            _loadedForUser = AccountState.UserName;

            foreach (var item in page.Items) History.Add(item);

            HistoryState.Hide();
            HistoryState.Visibility = Visibility.Collapsed;
            HistoryPanel.Visibility = Visibility.Visible;
            CountText.Text = _totalCount > 0 ? $"共 {_totalCount} 条" : string.Empty;

            if (History.Count == 0)
            {
                HistoryPanel.Visibility = Visibility.Collapsed;
                HistoryState.Visibility = Visibility.Visible;
                HistoryState.ShowEmpty(
                    "还没有播放记录",
                    "看过的视频会按时间倒序出现在这里。",
                    actionText: null,
                    glyph: "\uE81C");
                return;
            }

            // 还有下一页就给「加载更多」（接口也支持 totalCount 判断）
            bool hasMore = _totalCount > History.Count && page.Items.Count > 0;
            LoadMoreButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.WriteLog("UserPage.LoadHistory", ex);
            ShowHistoryError(ex.Message);
        }
        finally
        {
            _loading = false;
            MoreRing.IsActive = false;
            MoreRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowHistoryError(string? message)
    {
        HistoryPanel.Visibility = Visibility.Collapsed;
        HistoryState.Visibility = Visibility.Visible;
        HistoryState.ShowError("播放历史没加载出来", message ?? "网络或接口异常，请稍后重试。");
        LoadMoreButton.Visibility = Visibility.Collapsed;
    }

    private void HistoryState_ActionClick(object? sender, EventArgs e)
    {
        if (!AccountState.IsLoggedIn)
        {
            App.MainWindowInstance?.OpenOverlay(typeof(LoginPage));
            return;
        }

        _ = LoadHistoryAsync(reset: true);
    }

    private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadHistoryAsync(reset: false);
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not HistoryItem item) return;
        if (string.IsNullOrWhiteSpace(item.ResourceId)) return;

        Frame.Navigate(typeof(VideoPage), new VideoNavArgs
        {
            ResourceId = item.ResourceId,
            ResourceType = item.ResourceType,
            Title = item.Title,
            VideoId = string.IsNullOrWhiteSpace(item.VideoId) ? null : item.VideoId
        });
    }
}
