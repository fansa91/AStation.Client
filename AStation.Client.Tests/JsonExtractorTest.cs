using AStation_Client.Services;

namespace AStation_Client.Tests;

public class JsonExtractorTest
{
    [Fact]
    public void Extract_TakesBalancedObjectAfterMarker()
    {
        var source = "window.bangumiData = {\"title\":\"A\",\"nested\":{\"x\":1}}; var other = 1;";

        var json = JsonExtractor.Extract(source, "bangumiData");

        Assert.Equal("{\"title\":\"A\",\"nested\":{\"x\":1}}", json);
    }

    [Fact]
    public void Extract_TakesBalancedArrayAfterMarker()
    {
        var source = "var list = [1,[2,3],4]; tail";

        var json = JsonExtractor.Extract(source, "list");

        Assert.Equal("[1,[2,3],4]", json);
    }

    [Fact]
    public void Extract_ReturnsNullWhenMarkerMissing()
    {
        Assert.Null(JsonExtractor.Extract("window.data = {}", "videoInfo"));
        Assert.Null(JsonExtractor.Extract(null, "videoInfo"));
        Assert.Null(JsonExtractor.Extract("", "videoInfo"));
    }

    [Fact]
    public void Extract_IgnoresBracesInsideStrings()
    {
        // 大括号出现在字符串字面量里时不能参与配平，否则会提前截断或取错块
        var source = "var x = {\"a\":\"}{[\",\"b\":\"w \\\" }\"};";

        var json = JsonExtractor.Extract(source, "x");

        Assert.Equal("{\"a\":\"}{[\",\"b\":\"w \\\" }\"}", json);
    }

    [Fact]
    public void Extract_ReturnsNullWhenTruncated()
    {
        Assert.Null(JsonExtractor.Extract("window.data = {\"a\":1", "data"));
    }

    [Fact]
    public void ExtractProperty_TakesNamedObject()
    {
        var source = "{\"user\":{\"id\":1,\"name\":\"张三\"},\"other\":2}";

        var json = JsonExtractor.ExtractProperty(source, "user");

        Assert.Equal("{\"id\":1,\"name\":\"张三\"}", json);
    }

    [Fact]
    public void ExtractProperty_ReturnsNullWhenMissing()
    {
        Assert.Null(JsonExtractor.ExtractProperty("{\"a\":1}", "user"));
        Assert.Null(JsonExtractor.ExtractProperty(null, "user"));
    }
}
