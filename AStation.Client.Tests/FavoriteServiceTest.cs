using System.Text.Json.Nodes;
using AStation_Client.Services;

namespace AStation.Client.Tests;

/// <summary>
/// 收藏与公开资料的解析。
///
/// 收藏走 App 基址，响应模型是 FavoriteResponse{cursor, favoriteList[]}；
/// 但图片字段（contentImg / coverImage）、作者字段（userName / user.name）、
/// 列表字段名在不同分类上并不统一，解析必须逐个兜住。
/// </summary>
public class FavoriteServiceTest
{
    private readonly FavoriteService _service = new(new ApiService());

    private const string SampleJson = """
    {
      "result": 0,
      "cursor": "5",
      "favoriteList": [
        {
          "contentId": "28574231",
          "contentTitle": "快速看完2021MSI季中冠军赛Day3",
          "contentDesc": "赛事集锦",
          "contentImg": "https://imgs.aixifan.com/o_1f57puag51aia2mif4rrejaad0.jpeg",
          "coverImage": "https://imgs.aixifan.com/o_cover.jpeg",
          "groupId": 0,
          "views": 37532,
          "stows": 333,
          "comments": 50,
          "updateTime": 1620547098256,
          "userId": 16517519,
          "userName": "某某UP",
          "userImg": "https://imgs.aixifan.com/head.jpeg"
        },
        {
          "contentId": "30112345",
          "contentTitle": "没有封面的条目",
          "coverImage": "https://imgs.aixifan.com/o_cover2.jpeg",
          "user": { "name": "另一个UP" },
          "duration": 754
        }
      ]
    }
    """;

    [Fact]
    public void Parse_读取收藏条目字段()
    {
        var page = _service.Parse(JsonNode.Parse(SampleJson), FavoriteKind.Douga);

        Assert.Equal(0, page.Result);
        Assert.Equal(2, page.Items.Count);

        var first = page.Items[0];
        Assert.Equal("28574231", first.ContentId);
        Assert.Equal("快速看完2021MSI季中冠军赛Day3", first.Title);
        Assert.Equal("https://imgs.aixifan.com/o_1f57puag51aia2mif4rrejaad0.jpeg", first.Cover);
        Assert.Equal("某某UP", first.Up);
        Assert.Equal(37532, first.ViewCount);
        Assert.Equal(333, first.StowCount);
        Assert.Equal(FavoriteKind.Douga, first.Kind);
    }

    [Fact]
    public void Parse_缺字段时用备用字段兜住()
    {
        var page = _service.Parse(JsonNode.Parse(SampleJson), FavoriteKind.Douga);
        var second = page.Items[1];

        // contentImg 没有 → 用 coverImage
        Assert.Equal("https://imgs.aixifan.com/o_cover2.jpeg", second.Cover);
        // userName 没有 → 用 user.name
        Assert.Equal("另一个UP", second.Up);
        // duration 是秒 → 折成 mm:ss
        Assert.Equal("12:34", second.DurationText);
        Assert.True(second.HasDuration);
    }

    [Fact]
    public void Parse_游标与是否还有下一页()
    {
        var page = _service.Parse(JsonNode.Parse(SampleJson), FavoriteKind.Douga);

        Assert.Equal("5", page.Cursor);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Parse_空游标表示没有下一页()
    {
        var json = """{ "result": 0, "cursor": "", "favoriteList": [] }""";
        var page = _service.Parse(JsonNode.Parse(json), FavoriteKind.Douga);

        Assert.False(page.HasMore);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Parse_未登录返回负401()
    {
        var json = """{ "result": -401, "error_msg": "token value error" }""";
        var page = _service.Parse(JsonNode.Parse(json), FavoriteKind.Douga);

        Assert.Equal(-401, page.Result);
        Assert.Equal("token value error", page.ErrorMessage);
    }

    [Fact]
    public void Parse_列表包在data里也能读出来()
    {
        var json = """
        {
          "result": 0,
          "data": { "favoriteList": [ { "contentId": "1", "contentTitle": "包一层的数据" } ] }
        }
        """;
        var page = _service.Parse(JsonNode.Parse(json), FavoriteKind.Bangumi);

        Assert.Single(page.Items);
        Assert.Equal("包一层的数据", page.Items[0].Title);
    }

    [Fact]
    public void Parse_没有标题的条目直接跳过()
    {
        var json = """{ "result": 0, "favoriteList": [ { "contentId": "1" } ] }""";
        var page = _service.Parse(JsonNode.Parse(json), FavoriteKind.Album);

        Assert.Empty(page.Items);
    }

    [Fact]
    public void Parse_坏结构不抛异常()
    {
        var page = _service.Parse(JsonNode.Parse("""{ "result": 0, "favoriteList": "不是数组" }"""), FavoriteKind.Douga);

        Assert.Equal(0, page.Result);
        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData(FavoriteKind.Douga, "dougaList")]
    [InlineData(FavoriteKind.Bangumi, "bangumiList")]
    [InlineData(FavoriteKind.Album, "albumList")]
    [InlineData(FavoriteKind.Article, "articleList")]
    public void SlugOf_四个分类对应四条接口(FavoriteKind kind, string expected)
    {
        Assert.Equal(expected, FavoriteService.SlugOf(kind));
    }

    [Fact]
    public void 资源类型_视频是2番剧是1专辑文章没有对应页面()
    {
        Assert.Equal(2, new FavoriteItem { Kind = FavoriteKind.Douga }.NavigateResourceType);
        Assert.Equal(1, new FavoriteItem { Kind = FavoriteKind.Bangumi }.NavigateResourceType);
        Assert.Equal(0, new FavoriteItem { Kind = FavoriteKind.Album }.NavigateResourceType);
        Assert.Equal(0, new FavoriteItem { Kind = FavoriteKind.Article }.NavigateResourceType);
    }

    [Fact]
    public void 收藏时间_格式化成相对时间()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        Assert.Equal("刚刚", FavoriteService.FormatUpdateTime(now));
        Assert.Equal("5 分钟前", FavoriteService.FormatUpdateTime(now - 5 * 60 * 1000));
        Assert.Equal("3 小时前", FavoriteService.FormatUpdateTime(now - 3 * 3600 * 1000));
        Assert.Equal("2 天前", FavoriteService.FormatUpdateTime(now - 2 * 24 * 3600 * 1000));
        Assert.Equal(string.Empty, FavoriteService.FormatUpdateTime(0));
    }
}

/// <summary>用户公开资料解析：计数字段在 Web 端是字符串，要能吃。</summary>
public class UserProfileServiceTest
{
    private readonly UserProfileService _service = new(new ApiService());

    [Fact]
    public void Parse_字符串计数转成数字()
    {
        var json = """
        {
          "result": 0,
          "profile": {
            "name": "Dr-poet",
            "userId": 16517519,
            "headUrl": "https://imgs.aixifan.com/head.jpeg",
            "contentCount": "442",
            "likeCount": 377,
            "following": "3",
            "followed": "582",
            "verifiedText": "AcFun 认证用户",
            "registerTime": 1571286142000,
            "ipLocation": "北京",
            "shareUrl": "https://m.acfun.cn/upPage/16517519"
          }
        }
        """;

        var profile = _service.Parse(JsonNode.Parse(json));

        Assert.NotNull(profile);
        Assert.Equal("Dr-poet", profile!.Name);
        Assert.Equal(16517519, profile.UserId);
        Assert.Equal(442, profile.ContentCount);
        Assert.Equal(377, profile.LikeCount);
        Assert.Equal(3, profile.Following);
        Assert.Equal(582, profile.Followed);
        Assert.Equal("442 投稿 · 377 获赞 · 3 关注 · 582 粉丝", profile.StatsText);
        Assert.Contains("uid 16517519", profile.DetailText);
        Assert.Contains("IP 属地 北京", profile.DetailText);
    }

    [Fact]
    public void Parse_没有profile返回空()
    {
        Assert.Null(_service.Parse(JsonNode.Parse("""{ "result": 110, "error_msg": "用户不存在" }""")));
    }

    [Fact]
    public void 数字缩写_万和亿()
    {
        Assert.Equal("9999", MetricFormat.Count(9999));
        Assert.Equal("1.2万", MetricFormat.Count(12345));
        Assert.Equal("1.5亿", MetricFormat.Count(150_000_000));
    }

    [Fact]
    public void 收藏系resourceType_视频9番剧1()
    {
        // 收藏系与播放系不同：视频 9（播放系是 2），番剧 1
        Assert.Equal(9, FavoriteService.FavoriteResourceTypeOf(bangumi: false));
        Assert.Equal(1, FavoriteService.FavoriteResourceTypeOf(bangumi: true));
    }

    [Theory]
    [InlineData("""{"result":0,"isFavorite":true}""", true)]
    [InlineData("""{"result":0,"data":true}""", true)]
    [InlineData("""{"result":0,"isFavorite":{"28574231":true}}""", true)]
    [InlineData("""{"result":0,"data":{"28574231":true}}""", true)]
    [InlineData("""{"result":0,"isFavorite":false}""", false)]
    [InlineData("""{"result":0,"isFavorite":{"999999":true}}""", false)]
    public void 已收藏判定_各种响应形态(string json, bool expected)
    {
        var root = JsonNode.Parse(json)! as JsonObject;
        Assert.Equal(expected, FavoriteService.ReadFavoriteFlag(root!, "28574231"));
    }
}
