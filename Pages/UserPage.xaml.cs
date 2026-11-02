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
/// 数据来源：
///   · 账号卡统计 —— GET /rest/pc-direct/user/userInfo（匿名可调）
///   · 播放历史   —— POST /rest/pc-direct/browse/history/list（需登录）
///   · 收藏       —— POST api-new.app.acfun.cn/rest/app/favorite/{douga,bangumi,album,article}List（需登录）
///
/// 收藏是**游标**分页（cursor 首屏传 "0"），历史是页码分页，两者各管各的游标。
/// </summary>
public sealed partial class UserPage : Page
{
    private const int PageSize = 20;

    // 播放历史
    private int _pageNo;
    private int _totalCount;
    private bool _loading;
    private string? _loadedForUser;
    private int _tabIndex;

    // 收藏
    private FavoriteKind _kind = FavoriteKind.Douga;
    private string _favCursor = "0";
    private bool _favLoading;
    private bool _favHasMore;
    private bool _favLoaded;
    private string? _favLoadedForUser;

    public ObservableCollection<HistoryItem> History { get; } = new();

    public ObservableCollection<FavoriteItem> Favorites { get; } = new();

    public UserPage()
    {
        InitializeComponent();

        // 从历史点进视频再返回时，列表和滚动位置都要留住
        NavigationCacheMode = NavigationCacheMode.Enabled;

        HistoryList.ItemsSource = History;
        FavoriteList.ItemsSource = Favorites;
        ApplyLoggedInState();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ApplyLoggedInState();

        // 换了账号（或从没登录变成登录）才重新拉，单纯回退不重刷
        if (AccountState.IsLoggedIn && _loadedForUser != AccountState.UserName)
        {
            _ = LoadProfileAsync();
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

        if (!loggedIn)
        {
            UserStatsText.Visibility = Visibility.Collapsed;
            UserStatsText.Text = string.Empty;
        }

        if (!loggedIn && _tabIndex == 0)
        {
            ShowHistoryEmpty();
        }
    }

    /// <summary>拉公开资料，把投稿 / 获赞 / 关注 / 粉丝补到账号卡上（匿名可调，失败不影响其它）</summary>
    private async Task LoadProfileAsync()
    {
        long userId = AccountState.UserId;
        if (!AccountState.IsLoggedIn || userId <= 0) return;

        try
        {
            var profile = await ServiceLocator.UserProfile.GetProfileAsync(userId);
            if (profile is null) return;

            // 拉的过程中可能已经退出登录了
            if (!AccountState.IsLoggedIn || AccountState.UserId != userId) return;

            UserStatsText.Text = profile.StatsText;
            UserStatsText.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(profile.DetailText))
            {
                UserMetaText.Text = profile.DetailText;
            }

            if (!string.IsNullOrWhiteSpace(profile.Name) && !string.Equals(profile.Name, AccountState.UserName))
            {
                UserNameText.Text = profile.Name;
            }
        }
        catch (Exception ex)
        {
            App.WriteLog("UserPage.LoadProfile", ex);
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

        Favorites.Clear();
        _favLoaded = false;
        _favLoadedForUser = null;
        _favCursor = "0";
        _favHasMore = false;
        _kind = FavoriteKind.Douga;
        FavoriteMoreButton.Visibility = Visibility.Collapsed;
        ApplyKindChipStyles();

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
        HistoryState.Visibility = history && HistoryState.IsShowing
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollectPanel.Visibility = history ? Visibility.Collapsed : Visibility.Visible;

        if (history)
        {
            CountText.Text = _totalCount > 0 ? $"共 {_totalCount} 条" : string.Empty;
            if (!AccountState.IsLoggedIn) ShowHistoryEmpty();
        }
        else
        {
            CountText.Text = string.Empty;

            if (!AccountState.IsLoggedIn)
            {
                ShowCollectLoginState();
                return;
            }

            // 收藏是游标分页，切分类/换账号才重拉
            if (!_favLoaded || _favLoadedForUser != AccountState.UserName)
            {
                _ = LoadFavoritesAsync(reset: true);
            }
            else
            {
                ShowFavoritePanel();
            }
        }
    }

    private void ShowCollectLoginState()
    {
        // 筛选栏留着，只把列表换成引导态 —— 否则这里点不出别的分类
        CollectPanel.Visibility = Visibility.Visible;
        FavoriteListHost.Visibility = Visibility.Collapsed;
        CollectState.Visibility = Visibility.Visible;
        FavoriteMoreButton.Visibility = Visibility.Collapsed;
        CountText.Text = string.Empty;
        CollectState.ShowEmpty(
            "登录后才能看收藏",
            "AcFun 的收藏夹跟账号绑定，登录之后再来。",
            actionText: "去登录",
            glyph: "\uE77B");
    }

    /// <summary>收藏列表已有数据（或空）时，切换列表与空态的显示</summary>
    private void ShowFavoritePanel()
    {
        bool has = Favorites.Count > 0;

        CollectPanel.Visibility = Visibility.Visible;
        FavoriteListHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CollectState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = has ? $"已加载 {Favorites.Count} 条" : string.Empty;

        if (has) return;

        FavoriteMoreButton.Visibility = Visibility.Collapsed;
        CollectState.ShowEmpty(
            $"还没有收藏{KindName(_kind)}",
            _kind switch
            {
                FavoriteKind.Bangumi => "在番剧页点追番后，会出现在这里。",
                FavoriteKind.Album => "你收藏的专辑会出现在这里。",
                FavoriteKind.Article => "你收藏的文章会出现在这里。",
                _ => "在视频页点收藏后，会出现在这里。"
            },
            actionText: null,
            glyph: "\uE734");
    }

    private static string KindName(FavoriteKind kind) => kind switch
    {
        FavoriteKind.Bangumi => "番剧",
        FavoriteKind.Album => "专辑",
        FavoriteKind.Article => "文章",
        _ => "视频"
    };

    /// <summary>点分类 chip：只换样式、重拉列表，不重建控件</summary>
    private void CollectKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;

        if (!Enum.TryParse<FavoriteKind>(tag, out var kind)) return;
        if (kind == _kind && _favLoaded) return;

        _kind = kind;
        ApplyKindChipStyles();

        _ = LoadFavoritesAsync(reset: true);
    }

    private void ApplyKindChipStyles()
    {
        var selected = (Style)Application.Current.Resources["FilterChipSelectedStyle"];
        var normal = (Style)Application.Current.Resources["FilterChipStyle"];

        foreach (var (button, kind) in new[]
                 {
                     (KindDouga, FavoriteKind.Douga),
                     (KindBangumi, FavoriteKind.Bangumi),
                     (KindAlbum, FavoriteKind.Album),
                     (KindArticle, FavoriteKind.Article)
                 })
        {
            button.Style = kind == _kind ? selected : normal;
        }
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

    // ───────────────────────── 收藏 ─────────────────────────

    /// <summary>
    /// 拉一页收藏。<paramref name="reset"/> 为 false 时按游标续接（这个接口是 cursor 分页，不是页码）。
    /// </summary>
    private async Task LoadFavoritesAsync(bool reset)
    {
        if (_favLoading) return;
        if (!AccountState.IsLoggedIn)
        {
            ShowCollectLoginState();
            return;
        }

        _favLoading = true;

        if (reset)
        {
            _favCursor = "0";
            _favHasMore = false;
            Favorites.Clear();
            CountText.Text = string.Empty;
            CollectPanel.Visibility = Visibility.Visible;
            FavoriteListHost.Visibility = Visibility.Collapsed;
            CollectState.Visibility = Visibility.Visible;
            CollectState.ShowLoading($"正在读取收藏{KindName(_kind)}…");
            FavoriteMoreButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            FavoriteMoreButton.Visibility = Visibility.Collapsed;
            FavoriteRing.Visibility = Visibility.Visible;
            FavoriteRing.IsActive = true;
        }

        try
        {
            var page = await ServiceLocator.Favorite.GetFavoritesAsync(_kind, _favCursor, PageSize);

            if (page.Result == -401)
            {
                // 会话过期：标记与 Cookie 一起撤掉，页面回到未登录态
                SessionStore.Clear(ServiceLocator.Api);
                AccountState.Clear();
                ApplyLoggedInState();
                ShowCollectLoginState();
                return;
            }

            if (page.Result != 0)
            {
                CollectPanel.Visibility = Visibility.Visible;
                FavoriteListHost.Visibility = Visibility.Collapsed;
                CollectState.Visibility = Visibility.Visible;
                CollectState.ShowError(
                    $"收藏{KindName(_kind)}没加载出来",
                    page.ErrorMessage ?? "接口返回异常，请稍后重试。");
                FavoriteMoreButton.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var item in page.Items) Favorites.Add(item);

            _favCursor = page.Cursor;
            _favHasMore = page.HasMore;
            _favLoaded = true;
            _favLoadedForUser = AccountState.UserName;

            ShowFavoritePanel();
            FavoriteMoreButton.Visibility = _favHasMore ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.WriteLog("UserPage.LoadFavorites", ex);
            CollectPanel.Visibility = Visibility.Visible;
            FavoriteListHost.Visibility = Visibility.Collapsed;
            CollectState.Visibility = Visibility.Visible;
            CollectState.ShowError("收藏没加载出来", ex.Message);
        }
        finally
        {
            _favLoading = false;
            FavoriteRing.IsActive = false;
            FavoriteRing.Visibility = Visibility.Collapsed;
        }
    }

    private void FavoriteMoreButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadFavoritesAsync(reset: false);
    }

    private void FavoriteList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FavoriteItem item || !item.CanOpen) return;

        // 视频 → resourceType 2，番剧 → 1；专辑和文章还没有对应页面，点了不跳
        Frame.Navigate(typeof(VideoPage), new VideoNavArgs
        {
            ResourceId = item.ContentId,
            ResourceType = item.NavigateResourceType,
            Title = item.Title
        });
    }
}
