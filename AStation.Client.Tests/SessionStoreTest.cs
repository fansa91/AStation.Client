using System.Net;
using AStation_Client.Services;

namespace AStation_Client.Tests;

/// <summary>
/// SessionStore：桌面端会话落盘的外壳。重点是「存→读」保住 token、
/// 退出登录时容器与文件一起清干净，以及坏文件不把启动流程带崩。
/// </summary>
public class SessionStoreTest : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"astation-session-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void SaveThenLoad_ShouldRestoreSession()
    {
        using var first = new ApiService();
        first.GetCookieContainer().Add(
            new Uri("https://www.acfun.cn"),
            new Cookie("acPasstoken", "session-1", "/", ".acfun.cn"));

        Assert.Equal(_path, SessionStore.Save(first, _path));
        Assert.True(File.Exists(_path));

        using var second = new ApiService();
        Assert.Equal(1, SessionStore.Load(second, _path));
        Assert.Equal("session-1", second.GetCookieValue("https://www.acfun.cn", "acPasstoken"));
    }

    [Fact]
    public void Load_MissingFile_ShouldReturnZero()
    {
        using var api = new ApiService();
        Assert.Equal(0, SessionStore.Load(api, _path));
    }

    [Fact]
    public void Load_BrokenFile_ShouldReturnZeroWithoutThrowing()
    {
        File.WriteAllText(_path, "}{");

        using var api = new ApiService();
        Assert.Equal(0, SessionStore.Load(api, _path));
        Assert.Null(api.GetCookieValue("https://www.acfun.cn", "acPasstoken"));
    }

    [Fact]
    public void Clear_ShouldDropCookiesAndDeleteFile()
    {
        using var api = new ApiService();
        api.GetCookieContainer().Add(
            new Uri("https://www.acfun.cn"),
            new Cookie("acPasstoken", "session-2", "/", ".acfun.cn"));
        SessionStore.Save(api, _path);

        SessionStore.Clear(api, _path);

        Assert.False(File.Exists(_path));
        Assert.Null(api.GetCookieValue("https://www.acfun.cn", "acPasstoken"));
    }

    [Fact]
    public void Save_ShouldSkipExpiredCookies()
    {
        using var api = new ApiService();
        var container = api.GetCookieContainer();
        container.Add(new Uri("https://www.acfun.cn"), new Cookie("live", "1", "/", ".acfun.cn"));
        container.Add(new Uri("https://www.acfun.cn"), new Cookie("dead", "0", "/", ".acfun.cn")
        {
            Expires = DateTime.UtcNow.AddDays(-1)
        });

        SessionStore.Save(api, _path);

        using var restored = new ApiService();
        SessionStore.Load(restored, _path);
        Assert.Equal("1", restored.GetCookieValue("https://www.acfun.cn", "live"));
        Assert.Null(restored.GetCookieValue("https://www.acfun.cn", "dead"));
    }
}
