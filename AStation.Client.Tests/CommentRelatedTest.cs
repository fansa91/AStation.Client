using System.Text.Json.Nodes;
using AStation_Client.Services;
using Xunit;

namespace AStation_Client.Tests;

/// <summary>
/// 评论 / 相关视频 / CDN 线路解析测试。
/// 报文全部按实测接口返回裁剪而来（评论 commentId 是数字、头像在 headUrl 数组里），
/// 不要改成便于断言但不符合真实报文的形态 —— 之前弹幕测试就是这么把 bug 掩盖掉的。
/// </summary>
public class CommentRelatedTest
{
    // ───────────────────────── 评论 ─────────────────────────

    [Fact]
    public void ParseComments_RealShape_MapsAllFields()
    {
        var json = """
            {
              "result": 0,
              "commentCount": 51,
              "totalPage": 1,
              "curPage": 1,
              "stickyComments": [{
                "commentId": 807366900,
                "userName": "置顶UP",
                "content": "置顶评论",
                "likeCount": 12,
                "postDate": "1小时前",
                "timestamp": 1793109000000,
                "subCommentCount": 3,
                "isUp": true,
                "isSticky": true,
                "floor": 1,
                "headUrl": [{ "cdn": "tx-free-imgs.acfun.cn", "url": "https://example.com/sticky.jpg" }]
              }],
              "hotComments": [],
              "rootComments": [{
                "commentId": 807366971,
                "userId": 78313067,
                "userName": "严新红",
                "content": "123",
                "likeCount": 0,
                "postDate": "4分钟前",
                "timestamp": 1793109283327,
                "subCommentCount": 0,
                "isUp": false,
                "isSticky": false,
                "floor": 50,
                "replyToUserName": "",
                "headUrl": [{ "cdn": "tx-free-imgs.acfun.cn", "url": "https://example.com/a.jpg" }]
              }]
            }
            """;

        var obj = JsonNode.Parse(json)!.AsObject();
        var result = CommentService.ParseComments(obj, page: 1);

        Assert.Equal(51, result.TotalCount);
        Assert.Equal(1, result.TotalPage);
        Assert.Equal(2, result.Items.Count);

        // 置顶排在最前
        var sticky = result.Items[0];
        Assert.Equal(807366900, sticky.CommentId);
        Assert.True(sticky.IsSticky);
        Assert.True(sticky.IsUp);
        Assert.Equal("置顶UP", sticky.UserName);
        Assert.Equal("https://example.com/sticky.jpg", sticky.Avatar);
        Assert.Equal("3 条回复", sticky.SubCommentText);

        var normal = result.Items[1];
        Assert.Equal(807366971, normal.CommentId);
        Assert.Equal("123", normal.Content);
        Assert.Equal("4分钟前", normal.PostDate);
        Assert.Equal(50, normal.Floor);
        Assert.False(normal.IsUp);
        Assert.Equal("78313067", normal.UserId);
    }

    [Fact]
    public void ParseComments_DuplicateAcrossHotAndRoot_IsDeduped()
    {
        var json = """
            {
              "result": 0,
              "commentCount": 2,
              "totalPage": 1,
              "hotComments": [{ "commentId": 111, "userName": "A", "content": "热门" }],
              "rootComments": [
                { "commentId": 111, "userName": "A", "content": "热门" },
                { "commentId": 222, "userName": "B", "content": "普通" }
              ]
            }
            """;

        var result = CommentService.ParseComments(JsonNode.Parse(json)!.AsObject(), 1);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Items.Select(i => i.CommentId).Distinct().Count());
    }

    [Fact]
    public void ParseComments_AvatarFallsBackToThumbnailCdnUrls()
    {
        var json = """
            {
              "result": 0,
              "rootComments": [{
                "commentId": 333,
                "userName": "无headUrl",
                "content": "x",
                "userHeadImgInfo": {
                  "thumbnailImage": {
                    "cdnUrls": [{ "url": "https://example.com/thumb.jpg" }]
                  }
                }
              }]
            }
            """;

        var result = CommentService.ParseComments(JsonNode.Parse(json)!.AsObject(), 1);

        Assert.Single(result.Items);
        Assert.Equal("https://example.com/thumb.jpg", result.Items[0].Avatar);
    }

    [Fact]
    public void ParseComments_EntriesWithoutNameAndContent_AreSkipped()
    {
        var json = """
            {
              "result": 0,
              "rootComments": [
                { "commentId": 1 },
                { "commentId": 2, "userName": "有名字" }
              ]
            }
            """;

        var result = CommentService.ParseComments(JsonNode.Parse(json)!.AsObject(), 1);

        Assert.Single(result.Items);
        Assert.Equal(2, result.Items[0].CommentId);
    }

    // ───────────────────────── 楼中楼 ─────────────────────────

    [Fact]
    public void ParseComments_AttachesSubCommentsToTheirRoot()
    {
        // 报文按实测裁剪：subCommentsMap 的 key 是主楼 commentId（字符串），值是 { pcursor, subComments[] }
        var json = """
            {
              "result": 0,
              "commentCount": 4,
              "totalPage": 1,
              "rootComments": [
                { "commentId": 807363289, "userName": "楼主", "content": "主楼", "subCommentCount": 4, "floor": 3 },
                { "commentId": 807366971, "userName": "没人回", "content": "冷清", "subCommentCount": 0, "floor": 4 }
              ],
              "subCommentsMap": {
                "807363289": {
                  "pcursor": "807363625",
                  "subComments": [
                    { "commentId": 807363438, "userName": "甲", "content": "第一条", "postDate": "3小时前", "replyToUserName": "" },
                    { "commentId": 807363490, "userName": "乙", "content": "第二条", "postDate": "2小时前", "replyToUserName": "甲" },
                    { "commentId": 807363551, "userName": "丙", "content": "第三条", "postDate": "1小时前", "replyToUserName": "乙" }
                  ]
                }
              }
            }
            """;

        var result = CommentService.ParseComments(JsonNode.Parse(json)!.AsObject(), 1);

        var root = result.Items[0];
        Assert.Equal(3, root.Replies.Count);
        Assert.True(root.CanExpand);
        Assert.False(root.ShowPlainSubText);
        Assert.Equal("展开 3 条回复", root.ReplyToggleText);

        var reply = root.Replies[1];
        Assert.True(reply.IsReply);
        Assert.Equal("回复 @甲", reply.ReplyToText);
        Assert.True(reply.HasReplyTo);

        // 只给前 3 条，第 4 条要提示出来
        Assert.Equal("另有 1 条回复未下发", root.HiddenReplyText);
        Assert.True(root.HasHiddenReplies);

        // 没有楼中楼的主楼不该被影响
        var cold = result.Items[1];
        Assert.Empty(cold.Replies);
        Assert.False(cold.CanExpand);
        Assert.Empty(cold.HiddenReplyText);
    }

    [Fact]
    public void ParseComments_ReplyCountWithoutSubComments_ShowsPlainText()
    {
        // 接口有时只给 subCommentCount 不给 subCommentsMap，这时只能显示静态文本，不能给展开按钮
        var json = """
            {
              "result": 0,
              "rootComments": [
                { "commentId": 1, "userName": "A", "content": "有回复但没下发", "subCommentCount": 7 }
              ],
              "subCommentsMap": {}
            }
            """;

        var result = CommentService.ParseComments(JsonNode.Parse(json)!.AsObject(), 1);

        var item = result.Items[0];
        Assert.False(item.CanExpand);
        Assert.True(item.ShowPlainSubText);
        Assert.Equal("7 条回复", item.SubCommentText);
    }

    [Fact]
    public void CommentItem_IsExpanded_TogglesTextAndRaisesChange()
    {
        var item = new CommentItem { SubCommentCount = 2 };
        item.Replies.Add(new CommentItem { CommentId = 1, Content = "a" });
        item.Replies.Add(new CommentItem { CommentId = 2, Content = "b" });

        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("展开 2 条回复", item.ReplyToggleText);
        Assert.Equal("\uE70D", item.ReplyToggleGlyph);

        item.IsExpanded = true;

        Assert.Equal("收起回复", item.ReplyToggleText);
        Assert.Equal("\uE70E", item.ReplyToggleGlyph);
        Assert.Contains(nameof(CommentItem.IsExpanded), changed);
        Assert.Contains(nameof(CommentItem.ReplyToggleText), changed);
    }

    // ───────────────────────── 相关视频 ─────────────────────────

    [Fact]
    public void ParseFeed_RealShape_MapsAndSkipsSelf()
    {
        var json = """
            {
              "result": 0,
              "total": 7498,
              "feed": [
                {
                  "dougaId": 48942334,
                  "title": "7000款Demo里藏着3000个AI游戏",
                  "coverUrl": "https://example.com/cover1.jpg",
                  "durationMillis": 452303,
                  "viewCount": 1257,
                  "danmakuCount": 17,
                  "createTime": "5小时前",
                  "user": { "href": "549491", "name": "SteamParty" }
                },
                {
                  "dougaId": 48941266,
                  "title": "当前正在播的视频",
                  "coverUrl": "https://example.com/self.jpg",
                  "durationMillis": 328000,
                  "viewCount": 2916,
                  "user": { "name": "自己" }
                }
              ]
            }
            """;

        var list = RelatedService.ParseFeed(JsonNode.Parse(json)!, excludeDougaId: "48941266", max: 20);

        Assert.Single(list);
        var item = list[0];
        Assert.Equal("48942334", item.DougaId);
        Assert.Equal("7000款Demo里藏着3000个AI游戏", item.Title);
        Assert.Equal("SteamParty", item.Author);
        Assert.Equal("07:32", item.DurationText);
        Assert.Contains("SteamParty", item.SubText);
        Assert.Contains("播放", item.SubText);
    }

    [Fact]
    public void ParseFeed_RespectsMax()
    {
        var feed = string.Join(",", Enumerable.Range(1, 10)
            .Select(i => $$"""{ "dougaId": {{i}}, "title": "t{{i}}" }"""));

        var json = JsonNode.Parse($$"""{ "result": 0, "feed": [{{feed}}] }""");

        var list = RelatedService.ParseFeed(json, null, max: 3);

        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void ParseFeed_NonZeroResult_ReturnsEmpty()
    {
        var json = JsonNode.Parse("""{ "result": 11, "feed": [] }""");

        Assert.Empty(RelatedService.ParseFeed(json, null, 20));
    }

    // ───────────────────────── CDN ─────────────────────────

    [Theory]
    [InlineData("tx-video.acfun.cn", "腾讯云")]
    [InlineData("ali-video.acfun.cn", "阿里云")]
    [InlineData("ks-video.acfun.cn", "金山云")]
    [InlineData("hw-video.acfun.cn", "华为云")]
    [InlineData("some-other-cdn.example.com", "some-other-cdn")]
    public void DescribeCdn_MapsHostToReadableName(string host, string expected)
    {
        Assert.Equal(expected, PlayService.DescribeCdn(host));
    }

    [Fact]
    public void ParseQualities_CollectsAllBackupUrlsAsCdnSources()
    {
        // ksPlayJson 是「字符串化的 JSON」，这里直接构造已解包的节点
        var json = """
            {
              "adaptationSet": [{
                "representation": [{
                  "url": "https://tx-video.acfun.cn/a/1.m3u8?sign=x",
                  "backupUrl": [
                    "https://ali-video.acfun.cn/a/1.m3u8?auth_key=y",
                    "https://ks-video.acfun.cn/a/1.m3u8?sign=z"
                  ],
                  "width": 1920, "height": 1080, "maxBitrate": 6000,
                  "qualityLabel": "1080P60"
                }]
              }]
            }
            """;

        var list = PlayService.ParseQualities(JsonNode.Parse(json), isHevc: false);

        Assert.Single(list);
        var q = list[0];
        Assert.Equal("1080P60", q.Label);
        Assert.Equal("https://tx-video.acfun.cn/a/1.m3u8?sign=x", q.Url);
        Assert.Equal("https://ali-video.acfun.cn/a/1.m3u8?auth_key=y", q.BackupUrl);

        Assert.Equal(3, q.CdnSources.Count);
        Assert.Equal("腾讯云", q.CdnSources[0].Name);
        Assert.False(q.CdnSources[0].IsBackup);
        Assert.Equal("阿里云", q.CdnSources[1].Name);
        Assert.True(q.CdnSources[1].IsBackup);
        Assert.Equal("金山云", q.CdnSources[2].Name);
    }

    [Fact]
    public void ParseQualities_QualityWithOnlyMainUrl_HasSingleCdnSource()
    {
        var json = """
            {
              "adaptationSet": [{
                "representation": [{
                  "url": "https://tx-video.acfun.cn/a/1.m3u8",
                  "width": 1280, "height": 720, "maxBitrate": 2000,
                  "qualityLabel": "720P"
                }]
              }]
            }
            """;

        var list = PlayService.ParseQualities(JsonNode.Parse(json), isHevc: false);

        Assert.Single(list);
        Assert.Single(list[0].CdnSources);
        Assert.Null(list[0].BackupUrl);
    }
}
