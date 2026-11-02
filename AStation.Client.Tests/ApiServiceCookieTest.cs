using System.Net;
using AStation_Client.Services;

namespace AStation_Client.Tests;

/// <summary>
/// Cookie 落盘 / 读回：界面的登录态只在内存里，命令行每次都是新进程，
/// 靠这对方法把会话接上——所以这里要保证往返一致、坏文件不炸。
/// </summary>
public class ApiServiceCookieTest : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"astation-cookies-{Guid.NewGuid():N}.json");

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
    public void SaveThenLoad_ShouldKeepCookieValue()
    {
        using var first = new ApiService();
        first.GetCookieContainer().Add(
            new Uri("https://www.acfun.cn"),
            new Cookie("auth", "token-abc", "/", ".acfun.cn"));

        first.SaveCookies(_path);
        Assert.True(File.Exists(_path));

        using var second = new ApiService();
        Assert.Equal(1, second.LoadCookies(_path));
        Assert.Equal("token-abc", second.GetCookieValue("https://www.acfun.cn", "auth"));
    }

    [Fact]
    public void LoadCookies_MissingFile_ShouldReturnZero()
    {
        using var api = new ApiService();
        Assert.Equal(0, api.LoadCookies(_path));
    }

    [Fact]
    public void LoadCookies_BrokenJson_ShouldThrowOrZero_ButNotCorruptContainer()
    {
        File.WriteAllText(_path, "not json");

        using var api = new ApiService();

        // 坏文件会抛 JsonException（调用方自己兜），但容器不该被写脏
        Assert.ThrowsAny<Exception>(() => api.LoadCookies(_path));
        Assert.Null(api.GetCookieValue("https://www.acfun.cn", "auth"));
    }

    [Fact]
    public void SaveCookies_ShouldCreateMissingDirectory()
    {
        string nested = Path.Combine(Path.GetTempPath(), $"astation-{Guid.NewGuid():N}", "sub", "cookies.json");
        try
        {
            using var api = new ApiService();
            api.GetCookieContainer().Add(new Uri("https://www.acfun.cn"), new Cookie("a", "b"));
            api.SaveCookies(nested);
            Assert.True(File.Exists(nested));
        }
        finally
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(nested));
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
