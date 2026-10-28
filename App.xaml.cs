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

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "startup-error.log");

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        this.UnhandledException += (_, e) =>
        {
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
