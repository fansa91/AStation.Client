using System;
using System.IO;
using System.Text.Json;

namespace AStation_Client.Services;

/// <summary>
/// 客户端登录态缓存。
/// 只负责 UI 展示（标题栏 / 导航底部账号区）；真正的会话凭据仍由 <see cref="ApiService"/> 的 Cookie 容器持有。
/// 登录页成功后调用 <see cref="MarkLoggedIn"/>，退出登录调用 <see cref="Clear"/>。
/// </summary>
public static class AccountState
{
    private static readonly object SyncRoot = new();

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStation.Client",
        "account.json");

    private static bool _loaded;

    /// <summary>是否已登录（本地标记，不代表 Cookie 一定未过期）</summary>
    public static bool IsLoggedIn { get; private set; }

    /// <summary>昵称；未知时为 null</summary>
    public static string? UserName { get; private set; }

    /// <summary>头像地址；未知时为 null</summary>
    public static string? AvatarUrl { get; private set; }

    /// <summary>登录态变化（UI 线程触发）</summary>
    public static event EventHandler? Changed;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(StatePath)) return;

            var json = File.ReadAllText(StatePath);
            var state = JsonSerializer.Deserialize<PersistedState>(json);
            if (state is null) return;

            IsLoggedIn = state.IsLoggedIn;
            UserName = state.UserName;
            AvatarUrl = state.AvatarUrl;
        }
        catch
        {
            // 缓存坏了就当未登录，不影响启动
        }
    }

    public static void MarkLoggedIn(string? userName = null, string? avatarUrl = null)
    {
        EnsureLoaded();
        IsLoggedIn = true;
        if (!string.IsNullOrWhiteSpace(userName)) UserName = userName;
        if (!string.IsNullOrWhiteSpace(avatarUrl)) AvatarUrl = avatarUrl;

        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>补上昵称 / 头像（登录后异步拉取用户信息时调用）</summary>
    public static void UpdateProfile(string? userName, string? avatarUrl)
    {
        EnsureLoaded();
        if (!IsLoggedIn) return;

        var changed = false;
        if (!string.IsNullOrWhiteSpace(userName) && userName != UserName) { UserName = userName; changed = true; }
        if (!string.IsNullOrWhiteSpace(avatarUrl) && avatarUrl != AvatarUrl) { AvatarUrl = avatarUrl; changed = true; }
        if (!changed) return;

        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Clear()
    {
        IsLoggedIn = false;
        UserName = null;
        AvatarUrl = null;

        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void Save()
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var json = JsonSerializer.Serialize(new PersistedState
                {
                    IsLoggedIn = IsLoggedIn,
                    UserName = UserName,
                    AvatarUrl = AvatarUrl
                });
                File.WriteAllText(StatePath, json);
            }
        }
        catch
        {
            // 写失败不影响当前会话
        }
    }

    private sealed class PersistedState
    {
        public bool IsLoggedIn { get; set; }
        public string? UserName { get; set; }
        public string? AvatarUrl { get; set; }
    }
}
