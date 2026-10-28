using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 直播拉流服务。
///
/// AcFun 直播只给 FLV（HLS 会返回 129001），拉流要走完整的访客令牌链（实测）：
///   1. 访问 live.acfun.cn 拿到 _did Cookie
///   2. POST https://id.app.acfun.cn/rest/app/visitor/login  form: sid=acfun.api.visitor
///      → { result:0, userId:访客uid, "acfun.api.visitor_st":令牌 }
///   3. POST https://api.kuaishouzt.com/rest/zt/live/web/getPlayUrls
///      query: subBiz=mainApp&kpn=ACFUN_APP&kpf=PC_WEB&userId=&did=&acfun.api.visitor_st=
///      form : visitorId=主播uid&liveId=直播id&pullStreamType=FLV
///      → data.videoPlayRes（字符串化 JSON）→ liveAdaptiveManifest[0].adaptationSet.representation[]
///
/// 令牌带缓存，同一会话内复用；失败时清缓存下次重取。
/// </summary>
public class LivePlayService
{
    private readonly ApiService _api;
    private string? _visitorUid;
    private string? _visitorToken;

    public LivePlayService(ApiService api)
    {
        _api = api;
    }

    /// <summary>获取直播间的全部码率档位（FLV）</summary>
    public async Task<LiveStreamResult> GetStreamsAsync(string authorId, string liveId, CancellationToken ct = default)
    {
        var result = new LiveStreamResult();

        if (string.IsNullOrWhiteSpace(authorId) || string.IsNullOrWhiteSpace(liveId))
        {
            result.ErrorMessage = "缺少直播间信息";
            return result;
        }

        try
        {
            var did = await EnsureDeviceIdAsync(ct);
            var token = await EnsureVisitorTokenAsync(ct);

            if (string.IsNullOrWhiteSpace(token))
            {
                result.ErrorMessage = "拿不到直播访客令牌（接口可能调整）";
                return result;
            }

            var query = new Dictionary<string, string>
            {
                ["subBiz"] = "mainApp",
                ["kpn"] = "ACFUN_APP",
                ["kpf"] = "PC_WEB",
                ["userId"] = _visitorUid ?? "0",
                ["did"] = did ?? string.Empty,
                ["acfun.api.visitor_st"] = token!
            };

            var form = new Dictionary<string, string>
            {
                ["visitorId"] = authorId,
                ["liveId"] = liveId,
                ["pullStreamType"] = "FLV"
            };

            var response = await _api.PostFormToHostAsync<JsonNode>(
                ApiService.LiveStreamHost, "/rest/zt/live/web/getPlayUrls", form, query, ct);

            if (response is null)
            {
                result.ErrorMessage = "拉流接口无响应";
                return result;
            }

            var code = (int)VideoDetailService.ReadLong(response["result"]);
            if (code != 1 && response["data"] is null)
            {
                result.ErrorMessage = DescribeError(code);
                // 令牌可能过期，下次重新取
                _visitorToken = null;
                return result;
            }

            ParseVideoPlayRes(response["data"]?["videoPlayRes"], result);
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"拉流失败：{ex.Message}";
            _visitorToken = null;
            return result;
        }
    }

    /// <summary>解析 videoPlayRes（可能是字符串化 JSON）</summary>
    internal static void ParseVideoPlayRes(JsonNode? node, LiveStreamResult result)
    {
        JsonNode? root = node;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try { root = JsonNode.Parse(text); } catch { return; }
        }

        if (root is not JsonObject obj) return;

        result.StreamName = VideoDetailService.ReadString(obj["streamName"]) ?? string.Empty;

        if (obj["liveAdaptiveManifest"] is not JsonArray manifests) return;

        foreach (var manifest in manifests)
        {
            if (manifest?["adaptationSet"]?["representation"] is not JsonArray representations) continue;

            foreach (var rep in representations)
            {
                if (rep is null) continue;

                var url = VideoDetailService.ReadString(rep["url"]);
                if (string.IsNullOrWhiteSpace(url)) continue;

                result.Qualities.Add(new LiveStreamQuality
                {
                    Name = VideoDetailService.ReadString(rep["name"]) ?? $"档位{result.Qualities.Count + 1}",
                    Bitrate = (int)VideoDetailService.ReadLong(rep["bitrate"]),
                    Url = url!
                });
            }
        }

        result.Qualities.Sort((a, b) => a.Bitrate.CompareTo(b.Bitrate));

        if (result.Qualities.Count == 0)
        {
            result.ErrorMessage = "主播当前没有推流（或流已结束）";
        }
    }

    /// <summary>确保拿到 _did（访问直播站首页时服务端下发）</summary>
    private async Task<string?> EnsureDeviceIdAsync(CancellationToken ct)
    {
        var did = _api.GetCookieValue(ApiService.LiveSiteHost, "_did");
        if (!string.IsNullOrWhiteSpace(did)) return did;

        try
        {
            await _api.GetStringFromHostAsync(ApiService.LiveSiteHost, "/", null, ct);
        }
        catch
        {
            // 首页抓不到时继续尝试用已有 Cookie
        }

        return _api.GetCookieValue(ApiService.LiveSiteHost, "_did");
    }

    /// <summary>访客令牌（缓存复用）</summary>
    private async Task<string?> EnsureVisitorTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_visitorToken)) return _visitorToken;

        var form = new Dictionary<string, string> { ["sid"] = "acfun.api.visitor" };
        var response = await _api.PostIdAsync<JsonNode>("/rest/app/visitor/login", form, ct);
        if (response is null) return null;

        var code = (int)VideoDetailService.ReadLong(response["result"]);
        if (code != 0) return null;

        _visitorUid = VideoDetailService.ReadString(response["userId"]);
        _visitorToken = VideoDetailService.ReadString(response["acfun.api.visitor_st"]);
        return _visitorToken;
    }

    private static string DescribeError(int code) => code switch
    {
        129001 => "HLS 拉流失败（AcFun 直播只支持 FLV）",
        201646582 => "访客令牌非法（缺少 _did）",
        _ => $"拉流失败（result={code}）"
    };
}
