using System.Text.Json.Nodes;
using AStation_Client.Services;

namespace AStation_Client.Tests;

/// <summary>
/// 播放历史解析：接口字段名和首页卡片完全不同（durationSecondsShow 之类都是成品文案），
/// 而且数字/字符串混着给，解析要容忍；这里用文档里的真实响应结构兜底。
/// </summary>
public class HistoryServiceTest
{
    private const string SampleJson = """
    {
      "result": 0,
      "pageSize": 20,
      "totalCount": 210,
      "pageNo": 1,
      "histories": [
        {
          "resourceId": 28574231,
          "videoId": 24233958,
          "resourceType": 2,
          "title": "快速看完2021MSI季中冠军赛Day3",
          "cover": "https://imgs.aixifan.com/o_1f57puag51aia2mif4rrejaad0.jpeg",
          "durationSecondsShow": "07:26",
          "playedSecondsShow": "已看完",
          "durationSeconds": 446,
          "playedSeconds": 574,
          "browseTime": 1620547098256,
          "dougaVideoTitle": "IW vs PSG",
          "user": { "name": "某某UP" }
        },
        {
          "resourceId": "999",
          "videoId": "888",
          "resourceType": 1,
          "title": "番剧第一话",
          "cover": "",
          "durationSecondsShow": "24:00",
          "playedSecondsShow": "02:10",
          "durationSeconds": 1440,
          "playedSeconds": 130,
          "browseTime": 0,
          "bangumiItemEpisodeName": "第1话",
          "user": { "name": "" }
        }
      ]
    }
    """;

    private static HistoryPage Parse(string json)
        => new HistoryService(new ApiService()).Parse(JsonNode.Parse(json));

    [Fact]
    public void Parse_ShouldReadBothVideosAndBangumi()
    {
        var page = Parse(SampleJson);

        Assert.Equal(0, page.Result);
        Assert.Equal(210, page.TotalCount);
        Assert.Equal(2, page.Items.Count);

        var video = page.Items[0];
        Assert.Equal("28574231", video.ResourceId);
        Assert.Equal("24233958", video.VideoId);
        Assert.Equal(2, video.ResourceType);
        Assert.Equal("某某UP · IW vs PSG", video.MetaText);
        Assert.Equal("07:26", video.DurationText);
        Assert.Equal("已看完", video.ProgressText);

        // 已看完：进度 100 但不画进度条
        Assert.Equal(100, video.ProgressPercent);
        Assert.False(video.HasProgress);

        var bangumi = page.Items[1];
        Assert.Equal(1, bangumi.ResourceType);
        Assert.Equal("第1话", bangumi.Subtitle);
        Assert.Equal("第1话", bangumi.MetaText);   // UP 名为空，不留下孤零零的分隔符
        Assert.True(bangumi.HasProgress);
        Assert.InRange(bangumi.ProgressPercent, 9.0, 9.1);
    }

    [Fact]
    public void Parse_NotLoggedIn_ShouldKeepResultCode()
    {
        var page = Parse("""{"result":-401,"error_msg":"user not login"}""");

        Assert.Equal(-401, page.Result);
        Assert.Equal("user not login", page.ErrorMessage);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Parse_WebShapeWithoutResultField_ShouldSucceed()
    {
        // Web 端成功响应的顶层没有 result 字段（ACFUN_API.md 2026-11-02 实锤），
        // 只要 histories 是数组就算拿到数据，不能被当成失败。
        var page = Parse("""
        {
          "pageSize": 20,
          "histories": [
            {
              "resourceId": 28574231,
              "title": "某视频",
              "resourceType": 2,
              "durationSeconds": 100,
              "playedSeconds": 50
            }
          ],
          "totalCount": 1,
          "pageNo": 1
        }
        """);

        Assert.Equal(0, page.Result);
        Assert.Single(page.Items);
        Assert.Equal("某视频", page.Items[0].Title);
    }

    [Fact]
    public void Parse_NoResultAndNoHistories_ShouldBeFailure()
    {
        // 既没有 result=0 也没有 histories，才判失败
        var page = Parse("""{"pageNo":1,"pageSize":20}""");

        Assert.Equal(-1, page.Result);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Parse_BrokenShape_ShouldNotThrow()
    {
        var page = Parse("""{"result":0,"histories":[{"title":""},{"noTitle":1},null]}""");

        Assert.Equal(0, page.Result);
        Assert.Empty(page.Items);   // 没有标题的条目直接丢掉，界面不会出现空行
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    public void FormatBrowseTime_BadInput_ShouldReturnEmpty(long millis, string expected)
    {
        Assert.Equal(expected, HistoryService.FormatBrowseTime(millis));
    }

    [Fact]
    public void FormatBrowseTime_Today_ShouldUseHourMinute()
    {
        var today = new DateTimeOffset(DateTime.Today.AddHours(9).AddMinutes(5));
        var text = HistoryService.FormatBrowseTime(today.ToUnixTimeMilliseconds());

        Assert.Equal("今天 09:05", text);
    }

    [Fact]
    public void FormatBrowseTime_OlderThisYear_ShouldUseMonthDay()
    {
        var date = new DateTime(DateTime.Today.Year, 1, 2, 8, 0, 0);
        var text = HistoryService.FormatBrowseTime(
            new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local)).ToUnixTimeMilliseconds());

        Assert.Equal("1月2日", text);
    }
}
