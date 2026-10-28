using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using AStation_Client.Pages;
using AStation_Client.Pages.Overlay;
using AStation_Client.Services;

namespace AStation_Client;

public sealed partial class MainWindow : Window
{
    // ── 响应式断点（与 docs/ui-layout-plan.md §5 一致） ──
    private const double BreakpointSearchCollapse = 900;   // 搜索框收成图标
    private const double BreakpointNavCompact = 1000;      // 导航转紧凑（48px 图标栏）
    private const double BreakpointBrandHide = 1100;       // 隐藏应用名
    private const double BreakpointNavWide = 1200;         // 导航转展开（240px）
    private const double BreakpointContentWide = 1600;     // 内容限宽起点
    private const double BreakpointContentUltra = 1920;    // 超宽内容限宽

    private bool _isTopmost;
    private double _lastLayoutWidth = -1;
    private NavigationViewPaneDisplayMode _responsivePaneMode = NavigationViewPaneDisplayMode.Left;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        SettingsPage.ApplyTheme();

        // 导航按当前宽度决定展开 / 紧凑 / 覆盖
        UpdateResponsiveLayout(force: true);

        Activated += (_, _) => UpdateTitleBarInsets();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange) UpdateTitleBarInsets();
        };
        ShellRoot.SizeChanged += (_, _) => UpdateResponsiveLayout();
        MainFrame.Navigated += (_, e) =>
        {
            BackButton.IsEnabled = MainFrame.CanGoBack;
            SyncNavSelection(e.SourcePageType);
        };

        AccountState.EnsureLoaded();
        AccountState.Changed += (_, _) => RefreshAccountUi();
        RefreshAccountUi();

        ApplyInitialWindowSize();

        // 默认打开首页
        MainFrame.Navigate(typeof(HomePage));
    }

    /// <summary>
    /// 冷启动时的窗口尺寸。
    /// WinUI3 默认给的是一个偏小的窗口，播放页两栏布局会挤在一起，
    /// 所以启动时显式给一个按工作区分档的尺寸，并居中显示。
    /// </summary>
    private void ApplyInitialWindowSize()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;

            // 目标 1440×900，工作区放不下就按比例收，再兜底最小值
            int width = Math.Min(1440, (int)(work.Width * 0.86));
            int height = Math.Min(900, (int)(work.Height * 0.88));

            width = Math.Max(1000, width);
            height = Math.Max(680, height);

            // 极窄屏（如 1024×768）时上面的下限可能超出工作区，再夹一次
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);

            int x = work.X + (work.Width - width) / 2;
            int y = work.Y + (work.Height - height) / 2;

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("window", $"设置初始窗口尺寸失败：{ex.Message}");
        }
    }

    // ───────────── 响应式 ─────────────

    /// <summary>
    /// 按窗口宽度切换导航形态与标题栏密度。
    /// 设置里的「侧边栏常驻展开」只决定 ≥1200 时是否展开，不覆盖断点。
    /// </summary>
    private void UpdateResponsiveLayout(bool force = false)
    {
        double width = ShellRoot.ActualWidth;
        if (width <= 0) width = AppWindow.Size.Width;
        if (width <= 0) return;

        if (!force && Math.Abs(width - _lastLayoutWidth) < 2 && _lastLayoutWidth > 0) return;
        _lastLayoutWidth = width;

        // 标题栏密度：窄窗口只留图标，名称让位给搜索框
        AppBrandText.Visibility = width >= BreakpointBrandHide ? Visibility.Visible : Visibility.Collapsed;

        bool collapseSearch = width < BreakpointSearchCollapse;
        SearchBox.Visibility = collapseSearch ? Visibility.Collapsed : Visibility.Visible;
        SearchIconButton.Visibility = collapseSearch ? Visibility.Visible : Visibility.Collapsed;

        // 内容限宽（超宽屏居中，避免卡片被拉散）
        if (_chromeHidden)
        {
            ContentHost.MaxWidth = double.PositiveInfinity;
        }
        else if (width >= BreakpointContentUltra)
        {
            ContentHost.MaxWidth = BreakpointContentUltra;
        }
        else if (width >= BreakpointContentWide)
        {
            ContentHost.MaxWidth = BreakpointContentWide;
        }
        else
        {
            ContentHost.MaxWidth = double.PositiveInfinity;
        }

        if (_chromeHidden) return;   // 全屏期间不改导航

        var mode = width >= BreakpointNavWide
            ? NavigationViewPaneDisplayMode.Left
            : width >= BreakpointNavCompact
                ? NavigationViewPaneDisplayMode.LeftCompact
                : NavigationViewPaneDisplayMode.LeftMinimal;

        if (NavView.PaneDisplayMode != mode)
        {
            NavView.PaneDisplayMode = mode;
            _responsivePaneMode = mode;
            NavView.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left && AppSettings.Current.ExpandSidebar;
        }
    }

    /// <summary>
    /// 给系统窗口按钮（最小化/最大化/关闭）让位。
    /// 让位由标题栏最后一列的宽度承担，右侧内容不再改 Margin，
    /// 最大化 / 还原时不会出现元素横向跳动。
    /// </summary>
    private void UpdateTitleBarInsets()
    {
        double reserved = AppWindow.TitleBar.RightInset;

        // 少数情况下（窗口还没真正显示）拿到的 inset 是 0，用标准三按钮宽度兜底
        if (reserved <= 0) reserved = 138;

        if (Math.Abs(TitleBarInsetColumn.Width.Value - reserved) < 1) return;
        TitleBarInsetColumn.Width = new GridLength(reserved);
    }

    /// <summary>诊断用：把导航壳的布局状态拼成一行，排查全屏残留边距</summary>
    public string DescribeChrome()
    {
        try
        {
            var navOrigin = NavView.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            var gridOrigin = MainGrid.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            var frameOrigin = MainFrame.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            return $"mode={NavView.PaneDisplayMode} visible={NavView.IsPaneVisible} open={NavView.IsPaneOpen} " +
                   $"toggle={NavView.IsPaneToggleButtonVisible} compact={NavView.CompactPaneLength:F0} " +
                   $"nav=({navOrigin.X:F0},{navOrigin.Y:F0}) {NavView.ActualWidth:F0}x{NavView.ActualHeight:F0} " +
                   $"grid=({gridOrigin.X:F0},{gridOrigin.Y:F0}) {MainGrid.ActualWidth:F0}x{MainGrid.ActualHeight:F0} " +
                   $"frame=({frameOrigin.X:F0},{frameOrigin.Y:F0}) {MainFrame.ActualWidth:F0}x{MainFrame.ActualHeight:F0}";
        }
        catch (Exception ex)
        {
            return $"DescribeChrome 失败：{ex.Message}";
        }
    }

    /// <summary>侧边栏展开 / 收起（设置页调用）</summary>
    public void SetSidebarExpanded(bool expanded)
    {
        if (_chromeHidden) return;
        if (NavView.PaneDisplayMode == NavigationViewPaneDisplayMode.Left)
        {
            NavView.IsPaneOpen = expanded;
        }
    }

    // ───────────── 标题栏交互 ─────────────

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (MainFrame.CanGoBack) MainFrame.GoBack();
    }

    private void SearchIconButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverlay(typeof(SearchPage));
    }

    // ───────────── 账号区 ─────────────

    private void RefreshAccountUi()
    {
        bool logged = AccountState.IsLoggedIn;
        string name = string.IsNullOrWhiteSpace(AccountState.UserName) ? "我的账号" : AccountState.UserName!;

        NavItemAccount.Content = logged ? name : "登录";
        NavItemAccount.Icon = new FontIcon { Glyph = logged ? "\uE748" : "\uE77B" };

        AccountIcon.Glyph = logged ? "\uE748" : "\uE77B";
        ToolTipService.SetToolTip(AccountButton, logged ? name : "账号 / 登录");
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAccount();
    }

    private void OpenAccount()
    {
        if (AccountState.IsLoggedIn)
        {
            OpenOverlay(typeof(UserPage));
        }
        else
        {
            OpenOverlay(typeof(LoginPage));
        }
    }

    // ───────────── 导航 ─────────────

    /// <summary>
    /// 账号项设了 SelectsOnInvoked=False（不抢走页面高亮），点击不会触发 SelectionChanged，
    /// 只能靠 ItemInvoked 开覆盖层。
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag?.ToString() == "account")
        {
            OpenAccount();
        }
    }

    /// <summary>侧边栏导航选择变更</summary>
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            OpenMainPage(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            string tag = item.Tag?.ToString() ?? string.Empty;
            switch (tag)
            {
                case "home":
                    OpenMainPage(typeof(HomePage));
                    break;
                case "channel":
                    OpenMainPage(typeof(ChannelPage));
                    break;
                case "rank":
                    OpenMainPage(typeof(RankPage));
                    break;
                case "bangumi":
                    OpenMainPage(typeof(BangumiPage));
                    break;
                case "live":
                    OpenMainPage(typeof(LivePage));
                    break;
                case "settings":
                    OpenMainPage(typeof(SettingsPage));
                    break;
                case "account":
                    OpenAccount();
                    break;
                case "about":
                    OpenMainPage(typeof(AboutPage));
                    break;
                default:
                    OpenMainPage(typeof(HomePage));
                    break;
            }
        }
    }

    /// <summary>从覆盖层里跳主页面：先收覆盖层，再带着参数导航（搜索结果点卡片用）</summary>
    public void OpenMainPageFromOverlay(Type pageType, object? parameter)
    {
        CloseOverlay();
        MainFrame.Navigate(pageType, parameter);
        BackButton.IsEnabled = MainFrame.CanGoBack;
    }

    /// <summary>在主 Frame 打开页面（关闭覆盖层）</summary>
    private void OpenMainPage(Type pageType)
    {
        CloseOverlay();
        if (MainFrame.CurrentSourcePageType != pageType)
        {
            MainFrame.Navigate(pageType);
        }
        BackButton.IsEnabled = MainFrame.CanGoBack;
    }

    /// <summary>
    /// 页面可能不是从侧栏进来的（首页分区胶囊、频道卡片都会直接跳排行榜），
    /// 这时把侧栏选中项拉回当前页面，避免「高亮停在首页、内容却是排行榜」，
    /// 也避免再点首页时因为选中项没变而不触发切换。
    /// </summary>
    private void SyncNavSelection(Type pageType)
    {
        string? tag = pageType switch
        {
            var t when t == typeof(HomePage) => "home",
            var t when t == typeof(ChannelPage) => "channel",
            var t when t == typeof(RankPage) => "rank",
            var t when t == typeof(BangumiPage) => "bangumi",
            var t when t == typeof(LivePage) => "live",
            var t when t == typeof(SettingsPage) => "settings",
            var t when t == typeof(AboutPage) => "about",
            _ => null
        };

        if (tag is null) return;

        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (item.Tag?.ToString() != tag) continue;
            if (!ReferenceEquals(NavView.SelectedItem, item))
            {
                // 这里会再触发一次 SelectionChanged，但 OpenMainPage 判断了同页不重复导航，不会打转
                NavView.SelectedItem = item;
            }
            return;
        }
    }

    /// <summary>在覆盖层打开页面（搜索、登录、用户空间）</summary>
    public void OpenOverlay(Type pageType, object? parameter = null)
    {
        // 小窗口下卡片要跟着缩，不能按 880 硬撑
        double availableWidth = MainGrid.ActualWidth > 0 ? MainGrid.ActualWidth : 1200;
        double availableHeight = MainGrid.ActualHeight > 0 ? MainGrid.ActualHeight : 800;

        OverlayCard.MaxWidth = Math.Max(360, Math.Min(880, availableWidth - 48));
        OverlayCard.MaxHeight = Math.Max(280, Math.Min(760, availableHeight - 48));

        OverlayHost.Visibility = Visibility.Visible;
        OverlayFrame.Navigate(pageType, parameter);

        // 不再把 NavView 整体禁用：覆盖层挂在 ShellRoot 上，scrim 已经挡住下方交互，
        // 禁用会让背景内容连同文字一起变灰，看起来很脏。
    }

    /// <summary>关闭覆盖层</summary>
    public void CloseOverlay()
    {
        if (OverlayHost.Visibility == Visibility.Collapsed) return;

        OverlayHost.Visibility = Visibility.Collapsed;

        // 清空内容，避免下次打开沿用到旧页面状态
        if (OverlayFrame.Content is not null) OverlayFrame.Content = null;
    }

    private void OverlayScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CloseOverlay();
    }

    private void OverlayEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CloseOverlay();
        args.Handled = true;
    }

    // ───────────── 窗口置顶 ─────────────

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _isTopmost = !_isTopmost;

        SetWindowPos(hwnd,
            _isTopmost ? HwndTopmost : HwndNotTopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

        PinButton.Opacity = _isTopmost ? 1.0 : 0.62;
        ToolTipService.SetToolTip(PinButton, _isTopmost ? "取消置顶" : "窗口置顶");
    }

    // ───────────── 搜索 ─────────────

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            SearchBox.Text = string.Empty;
            OpenOverlay(typeof(SearchPage), args.QueryText.Trim());
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 预留：搜索联想
    }

    // ───────────── 全屏外壳 ─────────────

    private bool _paneOpenBeforeChromeHidden;
    private bool _paneToggleVisibleBeforeChromeHidden = true;

    /// <summary>
    /// 显示 / 隐藏窗口外壳（标题栏 + 侧边栏）。播放页全屏时调用，
    /// 让播放器真正铺满屏幕，而不是只铺满 NavigationView 的内容区。
    /// </summary>
    public void SetChromeVisible(bool visible)
    {
        if (!visible)
        {
            _paneOpenBeforeChromeHidden = NavView.IsPaneOpen;
            _paneToggleVisibleBeforeChromeHidden = NavView.IsPaneToggleButtonVisible;

            NavView.IsPaneOpen = false;
            NavView.IsPaneVisible = false;
            NavView.IsPaneToggleButtonVisible = false;

            ContentHost.MaxWidth = double.PositiveInfinity;

            // Left / Top 模式下 NavigationView 都会给内容区预留一条 CompactPaneLength(48px)
            // 的边（汉堡按钮的地盘），全屏时画面左边就是一条白边。
            // 直接在 NavigationView 上找不到「取消预留」的开关（把 CompactPaneLength 设 0 会崩），
            // 所以反过来做：把整个导航壳往左上平移这么多，再把它加宽加高同样多，
            // 内容区于是从 (0,0) 开始，铺满整个屏幕。
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyFullscreenBleed);
        }
        else
        {
            ResetFullscreenBleed();
            NavView.IsPaneVisible = true;
            NavView.IsPaneToggleButtonVisible = _paneToggleVisibleBeforeChromeHidden;

            // 导航形态按当前宽度重算，不还原全屏前的快照
            // （全屏时窗口尺寸变了，还原旧模式会出现「窄窗口却是展开态」）
            _lastLayoutWidth = -1;
            UpdateResponsiveLayout(force: true);
            NavView.IsPaneOpen = _paneOpenBeforeChromeHidden;
        }

        TitleBarRow.Height = visible ? new GridLength(48) : new GridLength(0);
        AppTitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _chromeHidden;
    private double _bleedX, _bleedY;

    /// <summary>全屏时把导航壳整体左上移，抵消 NavigationView 给内容区预留的边距</summary>
    private void ApplyFullscreenBleed()
    {
        _chromeHidden = true;

        try
        {
            if (ShellRoot.ActualWidth <= 0) return;

            var navOrigin = NavView.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            var gridOrigin = MainGrid.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));

            double dx = gridOrigin.X - navOrigin.X;
            double dy = gridOrigin.Y - navOrigin.Y;

            // 已经抵消过时再量就是 0，沿用上次的值（窗口尺寸变化时也要能重新套用）
            if (dx > 0 || dy > 0) { _bleedX = dx; _bleedY = dy; }
            if (_bleedX <= 0 && _bleedY <= 0) return;

            NavView.HorizontalAlignment = HorizontalAlignment.Left;
            NavView.VerticalAlignment = VerticalAlignment.Top;
            NavView.Margin = new Thickness(-_bleedX, -_bleedY, 0, 0);
            NavView.Width = ShellRoot.ActualWidth + _bleedX;
            NavView.Height = ShellRoot.ActualHeight + _bleedY;

            RuntimeLog.Write("window", $"全屏抵消内缩 dx={_bleedX:F0} dy={_bleedY:F0}（导航壳 {NavView.Width:F0}x{NavView.Height:F0}）");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("window", $"抵消内缩失败：{ex.Message}");
        }
    }

    private void ResetFullscreenBleed()
    {
        if (!_chromeHidden) return;
        _chromeHidden = false;

        NavView.Margin = new Thickness(0);
        NavView.Width = double.NaN;
        NavView.Height = double.NaN;
        NavView.HorizontalAlignment = HorizontalAlignment.Stretch;
        NavView.VerticalAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>
    /// 进入 / 退出「无边框全屏」。
    /// 这台系统是 Windows Server 2022（build 20348），AppWindow 的 FullScreen 呈现器
    /// 仍会给客户区留 1px 边框（XamlRoot 高度只有 1079），屏幕最上面就多一条白线；
    /// DWMWA_BORDER_COLOR 也不支持（返回 E_INVALIDARG）。
    /// 所以直接走 Win32：把窗口样式换成 WS_POPUP 再铺满整个显示器，客户区才等于屏幕。
    /// </summary>
    public void EnterBorderlessFullscreen()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var bounds = area.OuterBounds;

            if (_savedStyle is null)
            {
                _savedStyle = GetWindowLongPtr(hwnd, GwlStyle);
                _savedRect = new Windows.Graphics.RectInt32(
                    AppWindow.Position.X, AppWindow.Position.Y,
                    AppWindow.Size.Width, AppWindow.Size.Height);
            }

            long style = _savedStyle.Value.ToInt64();
            style &= ~(WsCaption | WsThickFrame | WsBorder | WsDlgFrame);
            style |= WsPopup | WsVisible;

            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow);

            _fullscreenBounds = bounds;
            RuntimeLog.Write("window", $"无边框全屏：{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");

            // WinUI 在随后的布局里可能又把窗口样式改回去，隔一帧再压一次
            _ = ReassertBorderlessStyleAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("window", $"进入无边框全屏失败：{ex.Message}");
        }
    }

    private Windows.Graphics.RectInt32 _fullscreenBounds;

    private async System.Threading.Tasks.Task ReassertBorderlessStyleAsync()
    {
        await System.Threading.Tasks.Task.Delay(300);
        if (_savedStyle is null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            long style = _savedStyle.Value.ToInt64();
            style &= ~(WsCaption | WsThickFrame | WsBorder | WsDlgFrame);
            style |= WsPopup | WsVisible;

            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero,
                _fullscreenBounds.X, _fullscreenBounds.Y, _fullscreenBounds.Width, _fullscreenBounds.Height,
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
            RuntimeLog.Write("window", $"无边框样式二次确认，窗口 {AppWindow.Size.Width}x{AppWindow.Size.Height}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("window", $"二次确认无边框失败：{ex.Message}");
        }
    }

    /// <summary>退出无边框全屏：还原窗口样式与位置</summary>
    public void ExitBorderlessFullscreen()
    {
        if (_savedStyle is null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindowLongPtr(hwnd, GwlStyle, _savedStyle.Value);
            SetWindowPos(hwnd, IntPtr.Zero, _savedRect.X, _savedRect.Y, _savedRect.Width, _savedRect.Height,
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
            RuntimeLog.Write("window", $"退出无边框全屏：还原 {_savedRect.Width}x{_savedRect.Height} @ {_savedRect.X},{_savedRect.Y}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("window", $"退出无边框全屏失败：{ex.Message}");
        }
        finally
        {
            _savedStyle = null;
        }
    }

    // ───────────── Win32：窗口置顶 ─────────────

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    // ───────────── Win32：窗口边框样式（全屏去白边） ─────────────

    private IntPtr? _savedStyle;
    private Windows.Graphics.RectInt32 _savedRect;

    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsBorder = 0x00800000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsPopup = 0x80000000L;
    private const long WsVisible = 0x10000000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
