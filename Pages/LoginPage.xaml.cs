using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using AStation_Client.Services;

namespace AStation_Client.Pages;

public sealed partial class LoginPage : Page
{
    private readonly LoginService _login = ServiceLocator.Login;
    private DispatcherTimer? _pollTimer;
    private string? _qrLoginToken;
    private string? _qrLoginSignature;
    private DateTime _qrExpireTime;

    // 图形验证码状态
    private string? _captchaKey;

    // 扫码轮询连续失败计数（服务端签名校验失败时优雅退出，而不是无声空转）
    private int _pollFailCount;

    public LoginPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await StartQrLoginAsync();
    }

    #region 扫码登录

    /// <summary>
    /// 启动扫码登录流程：获取二维码并开始轮询
    /// </summary>
    private async Task StartQrLoginAsync()
    {
        QrPlaceholder.Text = "加载二维码中…";
        QrStatusText.Text = "等待扫描…";
        QrCodeImage.Source = null;
        QrPlaceholder.Visibility = Visibility.Visible;
        LoginResultText.Visibility = Visibility.Collapsed;
        _pollFailCount = 0;

        try
        {
            var qrStart = await _login.QrStartAsync();
            if (qrStart?.Result != 0 || string.IsNullOrEmpty(qrStart.ImageData))
            {
                ShowQrUnavailable($"获取二维码失败（result={qrStart?.Result}），请点「刷新二维码」重试，或改用账号密码登录");
                return;
            }

            _qrLoginToken = qrStart.QrLoginToken;
            _qrLoginSignature = qrStart.QrLoginSignature;

            // 注意：接口返回的 expireTime 是「有效期毫秒数」（如 120000 = 2 分钟），
            // 不是 Unix 时间戳。直接当时间戳解析会得到 1970 年，导致二维码一出现就判过期。
            _qrExpireTime = qrStart.ExpireTime > 0
                ? DateTime.Now.AddMilliseconds(qrStart.ExpireTime)
                : DateTime.Now.AddMinutes(2);

            // 显示二维码图片
            await SetQrCodeImageAsync(qrStart.ImageData);
            QrPlaceholder.Visibility = Visibility.Collapsed;

            int minutes = Math.Max(1, (int)Math.Round((_qrExpireTime - DateTime.Now).TotalMinutes));
            QrStatusText.Text = $"等待扫描…（{minutes} 分钟内有效）";

            // 启动轮询（每 2 秒一次）
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _pollTimer.Tick += async (_, _) => await PollQrStatusAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            ShowQrUnavailable("网络异常，未能获取二维码，请重试或改用账号密码登录");
            App.WriteLog("LoginPage.StartQrLogin", ex);
        }
    }

    /// <summary>
    /// 扫码不可用时的界面状态
    /// </summary>
    private void ShowQrUnavailable(string message)
    {
        _pollTimer?.Stop();
        QrCodeImage.Source = null;
        QrPlaceholder.Text = message;
        QrPlaceholder.Visibility = Visibility.Visible;
        QrStatusText.Text = string.Empty;
    }

    /// <summary>
    /// 将 base64 图像数据设置为二维码图片源
    /// </summary>
    private async Task SetQrCodeImageAsync(string base64Image)
    {
        try
        {
            string base64 = base64Image;
            if (base64.Contains(","))
            {
                base64 = base64.Split(',', 2)[1];
            }

            var bytes = Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes);
            var randomAccessStream = stream.AsRandomAccessStream();

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(randomAccessStream);
            QrCodeImage.Source = bitmap;
        }
        catch
        {
            QrPlaceholder.Text = "二维码解析失败";
            QrPlaceholder.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 轮询扫码状态
    /// </summary>
    private async Task PollQrStatusAsync()
    {
        if (string.IsNullOrEmpty(_qrLoginToken) || string.IsNullOrEmpty(_qrLoginSignature))
        {
            return;
        }

        // 已过期，停止轮询
        if (DateTime.Now > _qrExpireTime)
        {
            _pollTimer?.Stop();
            QrPlaceholder.Text = "二维码已过期";
            QrPlaceholder.Visibility = Visibility.Visible;
            QrCodeImage.Source = null;
            QrStatusText.Text = "请刷新重新获取";
            return;
        }

        try
        {
            var result = await _login.QrScanResultAsync(_qrLoginToken, _qrLoginSignature);

            switch (result?.Result)
            {
                case 0:
                    // 已扫码，等待用户确认
                    _pollTimer?.Stop();
                    QrStatusText.Text = "已扫码，请在手机上确认登录";
                    QrStatusText.Foreground = App.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush;

                    // 接受扫码，换取登录 Cookie
                    await AcceptQrLoginAsync();
                    break;

                case 100400002:
                    // 已过期
                    _pollTimer?.Stop();
                    QrPlaceholder.Text = "二维码已过期";
                    QrPlaceholder.Visibility = Visibility.Visible;
                    QrCodeImage.Source = null;
                    QrStatusText.Text = "请刷新重新获取";
                    break;

                case 10:
                case 21:
                    // client error / sign error：服务端把签名与客户端网络环境绑定，
                    // 桌面端直连常被拒绝。连续多次失败就停轮询，避免无声空转。
                    _pollFailCount++;
                    if (_pollFailCount >= 3)
                    {
                        _pollTimer?.Stop();
                        QrStatusText.Text = "扫码状态校验未通过，请改用右侧账号密码登录";
                    }
                    break;

                default:
                    // 未扫码等中间态，继续等待
                    break;
            }
        }
        catch
        {
            // 网络错误等，继续下一次轮询
        }
    }

    /// <summary>
    /// 接受扫码并完成登录
    /// </summary>
    private async Task AcceptQrLoginAsync()
    {
        try
        {
            QrStatusText.Text = "正在确认登录…";

            var accept = await _login.QrAcceptResultAsync(_qrLoginToken!, _qrLoginSignature!);
            if (accept?.Result == 0)
            {
                QrStatusText.Text = "登录成功！";
                QrPlaceholder.Text = "";
                QrPlaceholder.Visibility = Visibility.Collapsed;
                LoginResultText.Text = "登录成功，页面将自动关闭";
                LoginResultText.Visibility = Visibility.Visible;

                // 关闭覆盖层
                CloseOverlayAfterDelay();
            }
            else
            {
                QrStatusText.Text = $"登录失败：{accept?.Result}";
            }
        }
        catch (Exception ex)
        {
            QrStatusText.Text = $"登录出错：{ex.Message}";
        }
    }

    private async void CloseOverlayAfterDelay()
    {
        await Task.Delay(1500);
        if (App.MainWindowInstance is MainWindow mainWindow)
        {
            mainWindow.CloseOverlay();
        }
    }

    private void RefreshQrButton_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer?.Stop();
        _qrLoginToken = null;
        _qrLoginSignature = null;
        _ = StartQrLoginAsync();
    }

    private void CancelQrButton_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer?.Stop();
        if (App.MainWindowInstance is MainWindow mainWindow)
        {
            mainWindow.CloseOverlay();
        }
    }

    #endregion

    #region 账号密码登录

    /// <summary>
    /// 登录按钮点击：若未获取验证码则先获取，否则直接调用 SignInAsync
    /// </summary>
    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        string password = PasswordBox.Password;
        string captchaCode = CaptchaBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ShowLoginResult("请输入账号和密码", isError: true);
            return;
        }

        // 如果尚未获取验证码，先获取验证码并显示面板
        if (CaptchaPanel.Visibility == Visibility.Collapsed)
        {
            await FetchCaptchaAsync();
            return;
        }

        // 已获取验证码，检查验证码输入
        if (string.IsNullOrWhiteSpace(captchaCode) || string.IsNullOrEmpty(_captchaKey))
        {
            ShowLoginResult("请输入图形验证码", isError: true);
            return;
        }

        // 调用账号密码登录
        await DoSignInAsync(username, password, _captchaKey, captchaCode);
    }

    /// <summary>
    /// 获取图形验证码并显示验证码面板
    /// </summary>
    private async Task FetchCaptchaAsync()
    {
        try
        {
            ShowLoginResult("正在获取验证码…", isError: false);

            var captcha = await _login.GetCaptchaAsync();
            if (captcha?.Result != 0 || string.IsNullOrEmpty(captcha.Key) || string.IsNullOrEmpty(captcha.Image))
            {
                ShowLoginResult($"获取验证码失败：{captcha?.Result}", isError: true);
                return;
            }

            _captchaKey = captcha.Key;

            // 解析 data:image/png;base64,...
            string base64 = captcha.Image;
            if (base64.Contains(","))
            {
                base64 = base64.Split(',', 2)[1];
            }

            var bytes = Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes);
            var randomAccessStream = stream.AsRandomAccessStream();

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(randomAccessStream);
            CaptchaImage.Source = bitmap;

            CaptchaPanel.Visibility = Visibility.Visible;
            ShowLoginResult("请输入图形验证码后点击登录", isError: false);
        }
        catch (Exception ex)
        {
            ShowLoginResult($"获取验证码出错：{ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// 执行账号密码登录
    /// </summary>
    private async Task DoSignInAsync(string username, string password, string captchaKey, string captchaCode)
    {
        try
        {
            SignInButton.Content = "登录中…";
            SignInButton.IsEnabled = false;

            var result = await _login.SignInAsync(username, password, captchaKey, captchaCode);

            if (result?.Result == 0)
            {
                ShowLoginResult("登录成功，页面将自动关闭", isError: false);
                CloseOverlayAfterDelay();
            }
            else
            {
                ShowLoginResult($"登录失败：{result?.ErrorMsg ?? result?.Result.ToString() ?? "未知错误"}", isError: true);
                // 登录失败后刷新验证码
                await FetchCaptchaAsync();
            }
        }
        catch (Exception ex)
        {
            ShowLoginResult($"登录出错：{ex.Message}", isError: true);
        }
        finally
        {
            SignInButton.Content = "登录";
            SignInButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 刷新图形验证码
    /// </summary>
    private async void RefreshCaptchaButton_Click(object sender, RoutedEventArgs e)
    {
        await FetchCaptchaAsync();
    }

    /// <summary>
    /// 显示登录结果提示
    /// </summary>
    private void ShowLoginResult(string message, bool isError)
    {
        LoginResultText.Text = message;
        LoginResultText.Visibility = Visibility.Visible;
        LoginResultText.Foreground = isError
            ? App.Current.Resources["SystemFillColorCriticalBrush"] as Microsoft.UI.Xaml.Media.Brush
            : App.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
    }

    #endregion
}
