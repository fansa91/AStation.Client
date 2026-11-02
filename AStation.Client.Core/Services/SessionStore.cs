using System.IO;

namespace AStation_Client.Services;

/// <summary>
/// 登录会话（Cookie）落盘。
///
/// 会话本钱是 Cookie，只活在 <see cref="ApiService"/> 的容器里，进程一退就没了；
/// 而 <see cref="AccountState"/> 的标记写在 account.json 里会活下来 —— 于是出现
/// 「重启后标题栏还显示已登录，点收藏却 401」的假登录。这里把整个容器存成 JSON，
/// 启动时先灌回去，退出登录时连同文件一起清掉。
///
/// 格式与命令行工具（<c>login</c> / <c>whoami</c>）用的是同一套 SaveCookies / LoadCookies。
/// </summary>
public static class SessionStore
{
    /// <summary>默认存放位置：%LOCALAPPDATA%\AStation.Client\cookies.json</summary>
    public static string CookiePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStation.Client",
        "cookies.json");

    /// <summary>把当前容器写到磁盘，返回实际路径；写失败返回 null（不影响本次会话）</summary>
    public static string? Save(ApiService api, string? path = null)
    {
        var target = path ?? CookiePath;
        try
        {
            api.SaveCookies(target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把磁盘上的 Cookie 灌回容器，返回载入条数（文件不存在 / 损坏都是 0）</summary>
    public static int Load(ApiService api, string? path = null)
    {
        try
        {
            return api.LoadCookies(path ?? CookiePath);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>退出登录：清空容器里的 Cookie 并删掉磁盘文件</summary>
    public static void Clear(ApiService api, string? path = null)
    {
        var target = path ?? CookiePath;
        try
        {
            api.ClearCookies();
        }
        catch
        {
            // 容器清理失败也要继续删文件
        }

        try
        {
            if (File.Exists(target)) File.Delete(target);
        }
        catch
        {
            // 文件被占用时留到下次覆盖
        }
    }
}
