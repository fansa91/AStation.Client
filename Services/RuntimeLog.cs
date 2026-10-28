using System;
using System.IO;
using System.Text;

namespace AStation_Client.Services;

/// <summary>
/// 极简运行日志。排查播放 / 弹幕这类"界面上看不出来"的问题时用，
/// 输出到 %LOCALAPPDATA%\AStation.Client\runtime.log（超过 256KB 自动清空）。
/// </summary>
public static class RuntimeLog
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStation.Client",
        "runtime.log");

    public static void Write(string tag, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            lock (Gate)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(LogPath);

                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志本身不能影响播放
        }
    }
}
