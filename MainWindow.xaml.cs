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
    private bool _isTopmost;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 恢复设置：侧边栏展开状态 + 主题
        NavView.IsPaneOpen = AppSettings.Current.ExpandSidebar;
        SettingsPage.ApplyTheme();

        Activated += (_, _) => UpdateTitleBarInsets();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange) UpdateTitleBarInsets();
        };

        ApplyInitialWindowSize();

        // 默认打开首页
        MainFrame.Navigate(typeof(HomePage));
    }

    /// <summary>
    /// 冷启动时的窗口尺寸。
    /// WinUI3 默认给的是一个偏小的窗口，播放页两栏布局（播放器 + 340px 侧栏）会挤在一起，
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

    /// <summary>
    /// 给系统窗口按钮（最小化/最大化/关闭）让位。
    /// 不让位的话，右侧的置顶、账号按钮会被压在窗口按钮下面。
    /// </summary>
    private void UpdateTitleBarInsets()
    {
        double reserved = AppWindow.TitleBar.RightInset;

        // 少数情况下（窗口还没真正显示）拿到的 inset 是 0，用标准三按钮宽度兜底
        if (reserved <= 0) reserved = 138;

        double right = Math.Max(16, reserved + 8);
        if (Math.Abs(TitleBarTail.Margin.Right - right) < 1) return;

        TitleBarTail.Margin = new Thickness(12, 0, right, 0);
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
        NavView.IsPaneOpen = expanded;
    }

    private bool _paneOpenBeforeChromeHidden;
    private bool _paneToggleVisibleBeforeChromeHidden = true;
    private NavigationViewPaneDisplayMode _paneDisplayModeBeforeChromeHidden = NavigationViewPaneDisplayMode.Left;

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
            _paneDisplayModeBeforeChromeHidden = NavView.PaneDisplayMode;

            NavView.IsPaneOpen = false;
            NavView.IsPaneVisible = false;
            NavView.IsPaneToggleButtonVisible = false;

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
            NavView.IsPaneToggleButtonVisible = _paneToggleVisibleBeforeChromeHidden;
            NavView.IsPaneVisible = true;
            NavView.IsPaneOpen = _paneOpenBeforeChromeHidden;
            NavView.PaneDisplayMode = _paneDisplayModeBeforeChromeHidden;
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

    // ───────────── 导航 / 覆盖层 / 标题栏交互 ─────────────

    /// <summary>
    /// 侧边栏导航选择变更
    /// </summary>
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
                case "login":
                    OpenMainPage(typeof(LoginPage));
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

    /// <summary>
    /// 在主 Frame 打开页面（关闭覆盖层）
    /// </summary>
    private void OpenMainPage(Type pageType)
    {
        CloseOverlay();
        MainFrame.Navigate(pageType);
    }

    /// <summary>
    /// 在覆盖层 Frame 打开页面（搜索、用户空间等）
    /// </summary>
    public void OpenOverlay(Type pageType)
    {
        OverlayFrame.Navigate(pageType);
        OverlayFrame.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        MainFrame.Opacity = 0.4;
        NavView.IsEnabled = false;
    }

    /// <summary>
    /// 关闭覆盖层
    /// </summary>
    public void CloseOverlay()
    {
        OverlayFrame.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        MainFrame.Opacity = 1.0;
        NavView.IsEnabled = true;
    }

    /// <summary>
    /// 账号按钮点击：打开登录页（覆盖层）
    /// </summary>
    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOverlay(typeof(LoginPage));
    }

    /// <summary>
    /// 置顶按钮：切换窗口 TopMost
    /// </summary>
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _isTopmost = !_isTopmost;

        SetWindowPos(hwnd,
            _isTopmost ? HwndTopmost : HwndNotTopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

        PinButton.Opacity = _isTopmost ? 1.0 : 0.72;
        ToolTipService.SetToolTip(PinButton, _isTopmost ? "取消置顶" : "窗口置顶");
    }

    /// <summary>
    /// 搜索框查询提交：打开搜索页（覆盖层）
    /// </summary>
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            OpenOverlay(typeof(SearchPage));
        }
    }

    /// <summary>
    /// 搜索框文本变更：可实现联想建议
    /// </summary>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 预留：搜索联想
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
