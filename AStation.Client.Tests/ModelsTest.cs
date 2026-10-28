using System.Text.Json;
using AStation_Client.Services;
using AStation_Client.ViewModels;

namespace AStation_Client.Tests;

public class ModelsTest
{
    /// <summary>
    /// 真实接口报文（2026-10 实测）：ksPlayJson 嵌套在 playInfo 里，而且值是「字符串化的 JSON」。
    /// 早期代码读顶层 ksPlayJson，导致永远拿不到播放地址。
    /// </summary>
    [Fact]
    public void PlayInfoResponse_Deserialize_RealPayload_ShouldMapCorrectly()
    {
        var innerJson = """
            {"videoId":"39803e6914868514","adaptationSet":[{"duration":307.6,"representation":[
            {"qualityLabel":"1080P60","url":"https://tx-video.acfun.cn/mediacloud/a/index.m3u8?sign=abc","backupUrl":["https://backup.acfun.cn/a/index.m3u8"],"width":1920,"height":1080,"maxBitrate":6000}]}]}
            """;

        var json = $$"""
            {
                "result": 0,
                "playInfo": {
                    "ksPlayJson": {{JsonSerializer.Serialize(innerJson)}},
                    "duration": 307.6,
                    "durationMillis": 307600,
                    "userPlayedSeconds": 0
                }
            }
            """;

        var response = JsonSerializer.Deserialize<PlayInfoResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(response);
        Assert.Equal(0, response.Result);
        Assert.NotNull(response.PlayInfo);
        Assert.Equal(307.6, response.PlayInfo!.Duration);

        // ksPlayJson 是字符串，业务层通过 PlayService.ParseQualities 二次解析
        var qualities = PlayService.ParseQualities(response.PlayInfo.KsPlayJson, isHevc: false);

        Assert.Single(qualities);
        Assert.Equal("1080P60", qualities[0].Label);
        Assert.Equal(1920, qualities[0].Width);
        Assert.Equal(6000, qualities[0].Bitrate);
        Assert.Contains("index.m3u8", qualities[0].Url);
        Assert.Equal("https://backup.acfun.cn/a/index.m3u8", qualities[0].BackupUrl);
    }

    /// <summary>
    /// 真实接口报文（2026-10 实测）：danmakuId / color 都是**数字**，
    /// 早期模型把它们写成 string，反序列化直接抛异常 → 弹幕列表永远为空。
    /// </summary>
    [Fact]
    public void DanmakuListResponse_Deserialize_RealPayload_ShouldMapCorrectly()
    {
        var json = """
            {
                "result": 0,
                "totalCount": 23,
                "danmakus": [{
                    "likeCount": 0,
                    "color": 16777215,
                    "mode": 1,
                    "roleId": 0,
                    "danmakuId": 287826045,
                    "danmakuType": 0,
                    "danmakuStyle": 1,
                    "danmakuAvatarUrl": "",
                    "isLike": false,
                    "rank": 5,
                    "body": "难得吃了顿好的",
                    "createTime": 1793101782864,
                    "position": 322609,
                    "userId": 6775629,
                    "danmakuImgUrl": "",
                    "size": 25
                }]
            }
            """;

        var response = JsonSerializer.Deserialize<DanmakuListResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(response);
        Assert.Equal(0, response.Result);
        Assert.Equal(23, response.TotalCount);
        Assert.Single(response.Danmakus);

        var item = response.Danmakus[0];
        Assert.Equal(287826045L, item.DanmakuId);
        Assert.Equal("难得吃了顿好的", item.Body);
        Assert.Equal(322609, item.PositionMs);
        Assert.Equal(16777215L, item.Color);
        Assert.Equal(1, item.Mode);
        Assert.Equal(25, item.Size);
        Assert.Equal(6775629L, item.UserId);
    }

    [Fact]
    public void NavigatorsResponse_Deserialize_ShouldMapCorrectly()
    {
        var json = """
            {
                "result": 0,
                "code": 200,
                "data": [{
                    "id": 1,
                    "navName": "综合",
                    "parent": 0,
                    "link": "/channel/1",
                    "cid": 1
                }, {
                    "id": 2,
                    "navName": "游戏",
                    "parent": 0,
                    "link": "/channel/2",
                    "cid": 2
                }]
            }
            """;

        var response = JsonSerializer.Deserialize<NavigatorsResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(response);
        Assert.Equal(0, response.Result);
        Assert.Equal(200, response.Code);
        Assert.Equal(2, response.Data.Count);
        Assert.Equal("综合", response.Data[0].NavName);
        Assert.Equal("游戏", response.Data[1].NavName);
    }

    [Fact]
    public void RankResponse_Deserialize_ShouldMapRealApiFields()
    {
        // 真实接口字段：contentTitle / userName / viewCount / coverUrl / duration(毫秒) / contentId
        var json = """
            {
                "result": 0,
                "rankList": [{
                    "contentId": 48940670,
                    "contentTitle": "测试视频",
                    "userName": "测试用户",
                    "userId": 12345,
                    "viewCount": 10000,
                    "duration": 358099,
                    "coverUrl": "https://example.com/cover.jpg"
                }]
            }
            """;

        var response = JsonSerializer.Deserialize<RankResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(response);
        Assert.Equal(0, response.Result);
        Assert.Single(response.RankList);

        var item = response.RankList[0];
        Assert.Equal("测试视频", item.Title);
        Assert.Equal("测试用户", item.UserName);
        Assert.Equal(12345, item.UserId);
        Assert.Equal(10000, item.ViewCount);
        Assert.Equal(358099, item.DurationMs);
        Assert.Equal("https://example.com/cover.jpg", item.Cover);
        Assert.Equal(48940670, item.ContentId);
    }

    [Theory]
    [InlineData(358099L, "05:58")]
    [InlineData(61500L, "01:01")]
    [InlineData(0L, "--:--")]
    [InlineData(null, "--:--")]
    [InlineData(3725000L, "1:02:05")]
    public void FormatDuration_ShouldProduceReadableText(long? ms, string expected)
    {
        Assert.Equal(expected, HomeViewModel.FormatDuration(ms));
    }

    [Theory]
    [InlineData(999L, "999")]
    [InlineData(1684L, "1684")]
    [InlineData(12500L, "1.3万")]
    [InlineData(0L, "0")]
    public void FormatCount_ShouldUseChineseUnits(long? count, string expected)
    {
        Assert.Equal(expected, HomeViewModel.FormatCount(count));
    }
}
