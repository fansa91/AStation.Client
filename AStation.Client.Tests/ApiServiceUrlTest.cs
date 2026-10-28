using AStation_Client.Services;

namespace AStation_Client.Tests;

public class ApiServiceUrlTest
{
    [Fact]
    public void BuildUrl_WithoutQueryParams_ShouldReturnBasePath()
    {
        var url = ApiService.BuildUrl("https://www.acfun.cn", "/rest/pc-direct/page/queryNavigators");
        Assert.Equal("https://www.acfun.cn/rest/pc-direct/page/queryNavigators", url);
    }

    [Fact]
    public void BuildUrl_WithQueryParams_ShouldAppendQueryString()
    {
        var query = new Dictionary<string, string>
        {
            ["resourceId"] = "39155377",
            ["resourceType"] = "2"
        };
        var url = ApiService.BuildUrl("https://www.acfun.cn", "/rest/pc-direct/play/playInfo/ksPlayJson", query);
        Assert.StartsWith("https://www.acfun.cn/rest/pc-direct/play/playInfo/ksPlayJson?resourceId=39155377&resourceType=2", url);
    }

    [Fact]
    public void BuildUrl_WithSpecialChars_ShouldEncodeQueryParams()
    {
        var query = new Dictionary<string, string>
        {
            ["keyword"] = "测试 关键词!"
        };
        var url = ApiService.BuildUrl("https://www.acfun.cn", "/rest/pc-direct/search/suggest", query);
        Assert.Contains("keyword=", url);
        // 特殊字符应被编码
        Assert.DoesNotContain(" ", url);
    }

    [Fact]
    public void BuildUrl_PathWithoutLeadingSlash_ShouldAddSlash()
    {
        var url = ApiService.BuildUrl("https://www.acfun.cn", "rest/pc-direct/test");
        Assert.Contains("/rest/pc-direct/test", url);
    }

    [Fact]
    public void BuildUrl_LiveHost_ShouldReturnCorrectUrl()
    {
        var url = ApiService.BuildUrl("https://api.kuaishouzt.com", "/rest/zt/live/web/getPlayUrls");
        Assert.Equal("https://api.kuaishouzt.com/rest/zt/live/web/getPlayUrls", url);
    }

    [Fact]
    public void BuildUrl_IdHost_ShouldReturnCorrectUrl()
    {
        var url = ApiService.BuildUrl("https://id.app.acfun.cn", "/rest/web/login/signin");
        Assert.Equal("https://id.app.acfun.cn/rest/web/login/signin", url);
    }
}
