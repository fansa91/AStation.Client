using AStation_Client.Services;

namespace AStation_Client.Tests;

public class HtmlParserServiceTest
{
    [Fact]
    public void ParseSearchResults_ExtractsFieldsFromExposureLog()
    {
        var html = "<a href=\"/v/ac7654321\" data-exposure-log=\"{&quot;content_id&quot;:&quot;1234567&quot;,&quot;authorId&quot;:&quot;888&quot;,&quot;title&quot;:&quot;测试视频&quot;,&quot;serverRId&quot;:&quot;2&quot;,&quot;serverGId&quot;:&quot;33&quot;}\">x</a>";

        var results = new HtmlParserService().ParseSearchResults(html);

        var item = Assert.Single(results);
        Assert.Equal("1234567", item.ContentId);
        Assert.Equal("888", item.AuthorId);
        Assert.Equal("测试视频", item.Title);
        Assert.Equal("2", item.ServerRId);
        Assert.Equal("33", item.ServerGId);
    }

    [Fact]
    public void ParseSearchResults_ParsesMultipleEntries()
    {
        var html =
            "<a data-exposure-log=\"{&quot;content_id&quot;:&quot;1&quot;,&quot;title&quot;:&quot;A&quot;}\">x</a>" +
            "<a data-exposure-log=\"{&quot;content_id&quot;:&quot;2&quot;,&quot;title&quot;:&quot;B&quot;}\">y</a>";

        var results = new HtmlParserService().ParseSearchResults(html);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].Title);
        Assert.Equal("B", results[1].Title);
    }

    [Fact]
    public void ParseSearchResults_SkipsMalformedEntry()
    {
        var html =
            "<a data-exposure-log=\"not-json\">x</a>" +
            "<a data-exposure-log=\"{&quot;content_id&quot;:&quot;9&quot;,&quot;title&quot;:&quot;好&quot;}\">y</a>";

        var results = new HtmlParserService().ParseSearchResults(html);

        var item = Assert.Single(results);
        Assert.Equal("9", item.ContentId);
        Assert.Equal("好", item.Title);
    }

    [Fact]
    public void ParseSearchResults_ReturnsEmptyWhenNoAttribute()
    {
        var results = new HtmlParserService().ParseSearchResults("<div>没有曝光标记的页面</div>");

        Assert.Empty(results);
    }

    [Fact]
    public void ExtractPageletScripts_ListsScriptStrings()
    {
        var html = "BigPipe.onPageletArrive({ html: \"x\", scripts: [\"/a.js\", \"/b.js\"] })";

        var scripts = new HtmlParserService().ExtractPageletScripts(html);

        Assert.Equal(new[] { "/a.js", "/b.js" }, scripts);
    }
}
