using AStation_Client.Models;
using AStation_Client.Services;

namespace AStation_Client.Tests;

/// <summary>
/// 直播分类 / 排行榜 / 番剧列表 三个解析器的单元测试。
/// 夹具均取自 2026-10 的真实响应（已裁剪为最小必要字段）。
/// </summary>
public class LiveRankBangumiParseTest
{
    // ---------- 直播分类：live.acfun.cn/api/channel/list ----------

    private const string LiveChannelJson = """
    {
      "channelListData": {
        "result": 0,
        "liveList": [
          {
            "title": "来咯！",
            "liveId": "W4XYRm5z8Bs",
            "href": "366012",
            "onlineCount": 60,
            "formatOnlineCount": "60",
            "coverUrls": [
              "https://ali-live.static.yximgs.com/bs2/ztlc/cover_W4XYRm5z8Bs_raw.jpg?imageView2/1/w/198/h/111",
              "https://js-live.static.yximgs.com/bs2/ztlc/cover_W4XYRm5z8Bs_raw.jpg"
            ],
            "type": { "id": 102, "name": "网游", "categoryId": 1, "categoryName": "游戏直播" },
            "user": { "id": "366012", "name": "按钮", "headUrl": "https://tx-free-imgs.acfun.cn/a.png" }
          },
          {
            "title": "画图",
            "liveId": "lOgXJgjofbY",
            "href": "34195163",
            "onlineCount": 55,
            "formatOnlineCount": "55",
            "coverUrls": ["https://ali-live.static.yximgs.com/bs2/ztlc/cover_lOgXJgjofbY_raw.jpg"],
            "type": { "id": 401, "name": "聊天", "categoryId": 4, "categoryName": "虚拟偶像" },
            "user": { "id": "34195163", "name": "野生东北虎王晴晴", "headUrl": "https://tx-free-imgs.acfun.cn/b.png" }
          }
        ],
        "totalCount": 13
      },
      "totalCount": 13,
      "channelFilters": {
        "liveChannelDisplayFilters": [
          {
            "displayFilters": [
              { "filterType": 1, "filterId": 0, "name": "全部" },
              { "filterType": 1, "filterId": 4, "name": "虚拟偶像", "cover": "https://imgs.aixifan.com/1" },
              { "filterType": 1, "filterId": 1, "name": "游戏", "cover": "https://imgs.aixifan.com/2" },
              { "filterType": 1, "filterId": 3, "name": "娱乐", "cover": "https://imgs.aixifan.com/3" },
              { "filterType": 1, "filterId": 2, "name": "其他", "cover": "https://imgs.aixifan.com/4" }
            ]
          }
        ]
      }
    }
    """;

    [Fact]
    public void ParseChannelList_ShouldReadItemsFromChannelListData()
    {
        var result = LiveService.ParseChannelList(LiveChannelJson);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(13, result.TotalCount);
    }

    [Fact]
    public void ParseChannelList_ShouldMapItemFields()
    {
        var result = LiveService.ParseChannelList(LiveChannelJson);

        var first = result.Items[0];
        Assert.Equal("来咯！", first.Title);
        Assert.Equal("按钮", first.Author);
        Assert.Equal(60, first.OnlineCount);
        Assert.Equal("60", first.OnlineText);
        Assert.Equal("W4XYRm5z8Bs", first.LiveId);
        Assert.Equal("366012", first.Link);
        Assert.Equal("网游", first.Tag);
        Assert.StartsWith("https://ali-live.static.yximgs.com/", first.Cover);
    }

    [Fact]
    public void ParseChannelList_ShouldReadCategoryFilters()
    {
        var result = LiveService.ParseChannelList(LiveChannelJson);

        Assert.Equal(5, result.Categories.Count);
        Assert.Equal(new[] { "全部", "虚拟偶像", "游戏", "娱乐", "其他" }, result.Categories.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { 0, 4, 1, 3, 2 }, result.Categories.Select(c => c.FilterId).ToArray());
    }

    [Fact]
    public void ParseChannelList_WhenFiltersMissing_ShouldFallbackToDefaults()
    {
        var result = LiveService.ParseChannelList("""{"channelListData":{"liveList":[]}}""");

        Assert.Empty(result.Items);
        Assert.Equal(5, result.Categories.Count);
        Assert.Equal("全部", result.Categories[0].Name);
        Assert.Equal(4, result.Categories[^1].FilterId);
    }

    [Fact]
    public void ParseChannelList_WithBrokenJson_ShouldReturnEmptyWithoutThrowing()
    {
        var result = LiveService.ParseChannelList("{not json");

        Assert.Empty(result.Items);
        Assert.Empty(result.Categories);
    }

    // ---------- 排行榜：/rest/pc-direct/rank/channel ----------

    private const string RankJson = """
    {
      "result": 0,
      "rankList": [
        {
          "contentTitle": "XBOX联名，一个比一个抽象",
          "userName": "Shi咪",
          "userId": 1773508,
          "viewCount": 1720,
          "danmuCount": 71,
          "duration": 358099,
          "videoCover": "https://tx-free-imgs.acfun.cn/newUpload/a.jpeg",
          "coverUrl": "https://tx-free-imgs.acfun.cn/newUpload/a.jpeg",
          "channelName": "生活日常",
          "contentId": 48940670,
          "dougaId": "48940670"
        },
        {
          "contentTitle": "《银松镇》是今年我最看好的独立游戏",
          "userName": "SteamParty",
          "userId": 934542,
          "viewCount": 18550,
          "danmuCount": 20,
          "duration": 328795,
          "videoCover": "https://tx-free-imgs.acfun.cn/newUpload/b.jpeg",
          "channelName": "主机单机",
          "contentId": 48941266
        }
      ]
    }
    """;

    [Fact]
    public void ParseRankList_ShouldAssignSequentialRank()
    {
        var entries = RankService.ParseRankList(System.Text.Json.Nodes.JsonNode.Parse(RankJson)!);

        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].Rank);
        Assert.Equal(2, entries[1].Rank);
        Assert.Equal("1", entries[0].RankText);
    }

    [Fact]
    public void ParseRankList_ShouldMapRealFieldNames()
    {
        var entries = RankService.ParseRankList(System.Text.Json.Nodes.JsonNode.Parse(RankJson)!);

        var first = entries[0];
        Assert.Equal("XBOX联名，一个比一个抽象", first.Title);
        Assert.Equal("Shi咪", first.Author);
        Assert.Equal("生活日常", first.ChannelName);
        Assert.Equal("48940670", first.ResourceId);
        Assert.Equal("1720", first.ViewText);
        Assert.Equal("71", first.DanmakuText);
        Assert.Equal("05:58", first.DurationText);
        Assert.Contains("生活日常", first.MetaText);
        Assert.Contains("播放", first.MetaText);
    }

    [Fact]
    public void ParseRankList_ShouldFormatLargeCounts()
    {
        var entries = RankService.ParseRankList(System.Text.Json.Nodes.JsonNode.Parse(RankJson)!);

        // 18550 -> 1.9万（一位小数）
        Assert.Equal("1.9万", entries[1].ViewText);
    }

    [Fact]
    public void ParseRankList_WhenRankListEmpty_ShouldReturnEmpty()
    {
        var entries = RankService.ParseRankList(System.Text.Json.Nodes.JsonNode.Parse("""{"result":0,"rankList":[]}""")!);

        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(RankPeriod.Today, "DAY", "日榜")]
    [InlineData(RankPeriod.ThreeDays, "THREE_DAYS", "三日榜")]
    [InlineData(RankPeriod.Week, "WEEK", "周榜")]
    public void ToPeriodParam_ShouldMatchApiValues(RankPeriod period, string expectedParam, string expectedLabel)
    {
        Assert.Equal(expectedParam, RankService.ToPeriodParam(period));
        Assert.Equal(expectedLabel, RankService.ToPeriodLabel(period));
    }

    // ---------- 番剧库：/bangumilist SSR HTML ----------

    private const string BangumiHtml = """
    <ul class="ac-mod-list"><li class="ac-mod-li" data-index="1" data-aid="6000617"><a href="https://www.acfun.cn/bangumi/aa6000617" class="ac-mod-link" target="_blank"><div class="ac-mod-cover-wrap"><img src="https://imgs.aixifan.com/lbss58eReZ.jpg?imageView2/1/w/296/h/394" class="ac-mod-cover" /><div class="ac-mod-cover-stats ac-mod-cover-icon-zhuifan">123.9万</div></div><div class="ac-mod-title" title="冰上的尤里"><span>冰上的尤里</span><br/><em>全12话</em></div></a></li><li class="ac-mod-li" data-index="2" data-aid="6000896"><a href="https://www.acfun.cn/bangumi/aa6000896" class="ac-mod-link" target="_blank"><div class="ac-mod-cover-wrap"><img src="https://imgs.aixifan.com/o37lRhobvV.jpg?imageView2/1/w/296/h/394" class="ac-mod-cover" /><div class="ac-mod-cover-stats ac-mod-cover-icon-zhuifan">114.8万</div></div><div class="ac-mod-title" title="达尔文游戏"><span>达尔文游戏</span><br/><em>全12话</em></div></a></li><li class="ac-mod-li" data-index="3" data-aid="5024869"><a href="https://www.acfun.cn/bangumi/aa5024869" class="ac-mod-link" target="_blank"><div class="ac-mod-cover-wrap"><img src="https://imgs.aixifan.com/MJJihvYUue.png?imageView2/1/w/296/h/394" class="ac-mod-cover" /><div class="ac-mod-cover-stats ac-mod-cover-icon-zhuifan">84.5万</div></div><div class="ac-mod-title" title="普通攻击是全体二连击，这样的妈妈你喜欢吗？"><span>普通攻击是全体二连击，这样的妈妈你喜...</span><br/><em>全13话</em></div></a></li></ul>
    """;

    [Fact]
    public void ParseBangumiList_ShouldExtractAllCards()
    {
        var items = BangumiService.ParseBangumiList(BangumiHtml);

        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { "6000617", "6000896", "5024869" }, items.Select(i => i.BangumiId).ToArray());
    }

    [Fact]
    public void ParseBangumiList_ShouldMapTitleCoverEpisodeAndStat()
    {
        var items = BangumiService.ParseBangumiList(BangumiHtml);

        var first = items[0];
        Assert.Equal("冰上的尤里", first.Title);
        Assert.Equal("全12话", first.EpisodeText);
        Assert.Equal(12, first.Episode);
        Assert.Equal("123.9万", first.StatText);
        Assert.Equal("https://www.acfun.cn/bangumi/aa6000617", first.Link);
        Assert.Contains("imgs.aixifan.com", first.Cover);
    }

    [Fact]
    public void ParseBangumiList_ShouldKeepFullTitleFromTitleAttribute()
    {
        var items = BangumiService.ParseBangumiList(BangumiHtml);

        // span 里是截断文本，title 属性才是全名
        Assert.Equal("普通攻击是全体二连击，这样的妈妈你喜欢吗？", items[2].Title);
    }

    [Fact]
    public void ParseBangumiList_WithHtmlWithoutCards_ShouldReturnEmpty()
    {
        Assert.Empty(BangumiService.ParseBangumiList("<html><body>nothing</body></html>"));
    }

    [Theory]
    [InlineData("全12话", 12)]
    [InlineData("更新至第8话", 8)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseEpisodeNumber_ShouldReadLeadingNumber(string? input, int expected)
    {
        Assert.Equal(expected, BangumiService.ParseEpisodeNumber(input));
    }
}
