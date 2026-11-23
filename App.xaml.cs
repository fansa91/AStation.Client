using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Text;

namespace AStation_Client;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>
    /// 主窗口实例。WinUI3 桌面应用没有 Window.Current，需通过这里访问主窗口。
    /// </summary>
    public static MainWindow? MainWindowInstance { get; private set; }

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStation.Client",
        "logs");

    private static readonly string LogPath = Path.Combine(LogDirectory, "app-errors.log");

    /// <summary>单文件上限：超过就轮转一次（当前 → .old），不让日志无限长大</summary>
    private const long LogRotateBytes = 2 * 1024 * 1024;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        this.UnhandledException += (_, e) =>
        {
            // XAML 树上的未处理异常：记录后继续运行。
            // 不区分可恢复性——没有可靠判据；对播放器来说，
            // 崩在后台请求的异常比带着日志继续跑更伤用户。
            // 异步链上的异常已由各页/服务的 try/catch 兜住，走到这里的都是意外。
            WriteLog("Application.UnhandledException", e.Exception);
            e.Handled = true;
        };

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            WriteLog("App.InitializeComponent", ex);
            throw;
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            MainWindowInstance = _window;
            _window.Activate();

            // 表情字典在后台预热：评论可以等，但弹幕是「渲染一次就过」的，
            // 等第一条弹幕出现时才去拉字典，那条弹幕里的表情就只能退成「[表情]」。
            _ = AStation_Client.Services.EmoteCatalog.Shared.EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            WriteLog("App.OnLaunched", ex);
            throw;
        }
    }

    internal static void WriteLog(string stage, Exception? ex)
    {
        try
        {
            // 日志放 %LOCALAPPDATA%\AStation.Client\logs：安装目录可能只读，
            // 而且升级覆盖安装也不会把日志弄丢
            Directory.CreateDirectory(LogDirectory);

            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > LogRotateBytes)
            {
                var oldPath = LogPath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(LogPath, oldPath);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}");
            if (ex is not null)
            {
                sb.AppendLine($"Type: {ex.GetType().FullName}");
                sb.AppendLine($"HResult: 0x{ex.HResult:X8}");
                sb.AppendLine($"Message: {ex.Message}");
                sb.AppendLine(ex.ToString());
            }
            File.AppendAllText(LogPath, sb.ToString() + Environment.NewLine);
        }
        catch
        {
            // 日志写入失败时忽略
        }
    }
}
