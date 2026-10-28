using AStation_Client.Services;

namespace AStation_Client.Tests;

public class ServiceLocatorTest
{
    [Fact]
    public void Api_ShouldReturnSingleton()
    {
        var api1 = ServiceLocator.Api;
        var api2 = ServiceLocator.Api;
        Assert.Same(api1, api2);
    }

    [Fact]
    public void Play_ShouldReturnSingleton()
    {
        var play1 = ServiceLocator.Play;
        var play2 = ServiceLocator.Play;
        Assert.Same(play1, play2);
    }

    [Fact]
    public void Danmaku_ShouldReturnSingleton()
    {
        var d1 = ServiceLocator.Danmaku;
        var d2 = ServiceLocator.Danmaku;
        Assert.Same(d1, d2);
    }

    [Fact]
    public void Comment_ShouldReturnSingleton()
    {
        var c1 = ServiceLocator.Comment;
        var c2 = ServiceLocator.Comment;
        Assert.Same(c1, c2);
    }

    [Fact]
    public void Login_ShouldReturnSingleton()
    {
        var l1 = ServiceLocator.Login;
        var l2 = ServiceLocator.Login;
        Assert.Same(l1, l2);
    }

    [Fact]
    public void HtmlParser_ShouldReturnSingleton()
    {
        var h1 = ServiceLocator.HtmlParser;
        var h2 = ServiceLocator.HtmlParser;
        Assert.Same(h1, h2);
    }

    [Fact]
    public void Services_ShouldShareSameApiInstance()
    {
        // 所有服务应该共享同一个 ApiService 实例
        var api = ServiceLocator.Api;
        Assert.Same(api, ServiceLocator.Play.GetApiForTest());
        Assert.Same(api, ServiceLocator.Danmaku.GetApiForTest());
        Assert.Same(api, ServiceLocator.Comment.GetApiForTest());
        Assert.Same(api, ServiceLocator.Login.GetApiForTest());
    }
}
