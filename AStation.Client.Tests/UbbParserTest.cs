using AStation_Client.Services;
using Xunit;

namespace AStation_Client.Tests;

/// <summary>
/// UBB 解析测试。
/// 用例里的标记全部来自实测评论正文（热门视频 65 条抽样）：
///   [emot=acfun,2789/]                    表情，最常见
///   [img=图片]https://imgs.aixifan.com/...[/img]  图片评论
///   [at uid=23682490]@AC娘本体[/at]          @ 某人
/// </summary>
public class UbbParserTest
{
    /// <summary>离线解析器：不依赖网络 / 缓存，只认这两张表情</summary>
    private static string? Lookup(long id) => id switch
    {
        2789 => "https://tx-free-imgs2.acfun.cn/bs2/zt-image-host/08faa987e90810eebdfd9902",
        1738 => "https://tx-free-imgs2.acfun.cn/bs2/acfun-image/1738_645f0e748bd9438bb64ccc6ee9167bc3",
        _ => null
    };

    [Fact]
    public void Parse_EmoteOnly_ProducesSingleEmoteSegment()
    {
        var segments = UbbParser.Parse("[emot=acfun,2789/]", Lookup);

        var segment = Assert.Single(segments);
        Assert.Equal(UbbSegmentKind.Emote, segment.Kind);
        Assert.StartsWith("https://tx-free-imgs2.acfun.cn/", segment.Url);
    }

    [Fact]
    public void Parse_TextAndEmote_KeepsTextAroundEmote()
    {
        var segments = UbbParser.Parse("超哥怒砍不孝子！[emot=acfun,1738/]", Lookup);

        Assert.Equal(2, segments.Count);
        Assert.Equal(UbbSegmentKind.Text, segments[0].Kind);
        Assert.Equal("超哥怒砍不孝子！", segments[0].Text);
        Assert.Equal(UbbSegmentKind.Emote, segments[1].Kind);
    }

    [Fact]
    public void Parse_TwoEmotesInARow_BothKept()
    {
        var segments = UbbParser.Parse("拿着的是[emot=acfun,1738/][emot=acfun,2789/]！", Lookup);

        Assert.Equal(4, segments.Count);
        Assert.Equal(2, segments.Count(s => s.Kind == UbbSegmentKind.Emote));
        Assert.Equal("！", segments[3].Text);
    }

    [Fact]
    public void Parse_UnknownEmoteId_FallsBackToPlaceholderText()
    {
        // 字典里没有的 id 不能挂一张 404 图，要退成文字占位
        var segments = UbbParser.Parse("笑死[emot=acfun,999999/]", Lookup);

        var segment = Assert.Single(segments);
        Assert.Equal(UbbSegmentKind.Text, segment.Kind);
        Assert.Equal("笑死[表情]", segment.Text);
    }

    [Fact]
    public void Parse_LegacyAcPackage_UsesUmeditorGif()
    {
        var segments = UbbParser.Parse("[emot=ac,13/]", Lookup);

        var segment = Assert.Single(segments);
        Assert.Equal(UbbSegmentKind.Emote, segment.Kind);
        Assert.Equal(
            "https://cdn.aixifan.com/dotnet/20130418/umeditor/dialogs/emotion/images/ac/13.gif",
            segment.Url);
    }

    [Fact]
    public void Parse_AtMention_BecomesPlainText()
    {
        var segments = UbbParser.Parse("劲啊！！！！[at uid=23682490]@AC娘本体[/at] ", Lookup);

        // @ 与前后文字都是纯文本，解析后会合并成一段
        var segment = Assert.Single(segments);
        Assert.Equal(UbbSegmentKind.Text, segment.Kind);
        Assert.Equal("劲啊！！！！@AC娘本体", segment.Text.Trim());
    }

    [Fact]
    public void Parse_ImageComment_ProducesImageSegment()
    {
        var segments = UbbParser.Parse(
            "看这个[img=图片]https://imgs.aixifan.com/content/2020_10_31/1.png[/img]",
            Lookup);

        Assert.Equal(2, segments.Count);
        Assert.Equal(UbbSegmentKind.Image, segments[1].Kind);
        Assert.Equal("https://imgs.aixifan.com/content/2020_10_31/1.png", segments[1].Url);
    }

    [Fact]
    public void Parse_NonHttpImageUrl_Dropped()
    {
        // 防止 javascript: / data: 之类的地址混进 Image.Source
        var segments = UbbParser.Parse("[img]javascript:alert(1)[/img]", Lookup);

        Assert.DoesNotContain(segments, s => s.Kind == UbbSegmentKind.Image);
    }

    [Fact]
    public void Parse_LineBreaks_SplitIntoSegments()
    {
        var segments = UbbParser.Parse("第一行\r\n第二行[br]第三行", Lookup);

        Assert.Equal(2, segments.Count(s => s.Kind == UbbSegmentKind.LineBreak));
        var texts = segments.Where(s => s.Kind == UbbSegmentKind.Text).Select(s => s.Text).ToArray();
        Assert.Equal(new[] { "第一行", "第二行", "第三行" }, texts);
    }

    [Fact]
    public void Parse_StyleTags_HaveNoLeftoverBrackets()
    {
        var segments = UbbParser.Parse("[b]粗[/b][color=#ff0000]红[/color][size=3]大[/size]", Lookup);

        string text = string.Concat(segments.Where(s => s.Kind == UbbSegmentKind.Text).Select(s => s.Text));
        Assert.Equal("粗红大", text);
        Assert.DoesNotContain("[", text);
    }

    [Fact]
    public void Parse_PlainText_IsSingleTextSegment()
    {
        var segments = UbbParser.Parse("ac娘哪有这么大", Lookup);

        var segment = Assert.Single(segments);
        Assert.Equal(UbbSegmentKind.Text, segment.Kind);
        Assert.Equal("ac娘哪有这么大", segment.Text);
    }

    [Fact]
    public void HasMarkup_DetectsOnlyRealMarkup()
    {
        Assert.True(UbbParser.HasMarkup("[emot=acfun,2789/]"));
        Assert.True(UbbParser.HasMarkup("[img]https://a.b/c.png[/img]"));
        Assert.True(UbbParser.HasMarkup("[at uid=1]@x[/at]"));
        Assert.True(UbbParser.HasMarkup("第一行\n第二行"));
        Assert.False(UbbParser.HasMarkup("普通弹幕 [普通方括号] 不算"));
        Assert.False(UbbParser.HasMarkup(""));
        Assert.False(UbbParser.HasMarkup(null));
    }

    [Fact]
    public void ToPlainText_ReplacesEmoteAndImage()
    {
        string text = UbbParser.ToPlainText("开场[emot=acfun,2789/]然后[img]https://a.b/c.png[/img]结束");

        Assert.Equal("开场[表情]然后[图片]结束", text);
    }

    // ───────────────────────── 字典解析 ─────────────────────────

    [Fact]
    public void ParsePackages_RealShape_MapsIdToBigImage()
    {
        // 按 /rest/pc-direct/emotion/getUserEmotion 的真实形态裁剪
        var json = """
            {
              "result": 0,
              "emotionPackageList": [
                {
                  "name": "新娘",
                  "id": 49,
                  "emotions": [
                    {
                      "packageId": 49,
                      "name": "笑",
                      "id": 1738,
                      "bigImageInfo": {
                        "thumbnailImage": {
                          "cdnUrls": [{ "url": "http://tx-free-imgs2.acfun.cn/bs2/acfun-image/1738_abc" }]
                        }
                      },
                      "smallImageInfo": {
                        "thumbnailImage": { "cdnUrls": [{ "url": "http://tx-free-imgs2.acfun.cn/bs2/acfun-image/1738_small" }] }
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var list = EmoteCatalog.ParsePackages(System.Text.Json.Nodes.JsonNode.Parse(json));

        var entry = Assert.Single(list);
        Assert.Equal(1738, entry.Id);
        Assert.Equal("新娘", entry.Package);
        Assert.Equal("笑", entry.Name);
        // http 要升到 https，否则 WinUI 的图片加载会被拦
        Assert.StartsWith("https://", entry.Url);
    }

    [Fact]
    public void ParsePackages_MissingPackages_ReturnsEmpty()
    {
        var list = EmoteCatalog.ParsePackages(System.Text.Json.Nodes.JsonNode.Parse("""{"result":1}"""));
        Assert.Empty(list);
    }
}
