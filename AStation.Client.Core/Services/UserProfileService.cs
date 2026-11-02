using System.Text.Json.Nodes;

namespace AStation_Client.Services;

/// <summary>
/// 用户公开资料（匿名可查，不需要 Cookie）。
///
/// 接口：GET /rest/pc-direct/user/userInfo?userId={uid}
///   响应: { result: 0, profile: { ...30 键... } }
///   用户不存在: { result: 110, error_msg: "用户不存在" }
///
/// 同一个 profile 结构在 App 端 /rest/app/user/userInfo 也适用，
/// 两边字段名一致，所以只实现 Web 一条路径。
/// </summary>
public class UserProfileService
{
    private const string Path = "/rest/pc-direct/user/userInfo";

    private readonly ApiService _api;

    public UserProfileService(ApiService api)
    {
        _api = api;
    }

    public async Task<UserProfile?> GetProfileAsync(long userId, CancellationToken ct = default)
    {
        if (userId <= 0) return null;

        try
        {
            var node = await _api.GetAsync<JsonNode>(
                Path,
                new Dictionary<string, string> { ["userId"] = userId.ToString() },
                ct);

            return Parse(node);
        }
        catch
        {
            return null;
        }
    }

    internal UserProfile? Parse(JsonNode? node)
    {
        if (node is not JsonObject root) return null;
        if (root["profile"] is not JsonObject profile) return null;

        return new UserProfile
        {
            UserId = ReadLong(profile, "userId"),
            Name = ReadString(profile, "name"),
            Avatar = ReadString(profile, "headUrl"),
            SpaceImage = ReadString(profile, "spaceImage"),
            ContentCount = ReadInt(profile, "contentCount"),
            LikeCount = ReadInt(profile, "likeCount"),
            Following = ReadInt(profile, "following"),
            Followed = ReadInt(profile, "followed"),
            VerifiedText = ReadString(profile, "verifiedText"),
            RegisterTimeMs = ReadLong(profile, "registerTime"),
            IpLocation = ReadString(profile, "ipLocation"),
            ShareUrl = ReadString(profile, "shareUrl")
        };
    }

    private static string ReadString(JsonNode? node, string name)
    {
        try
        {
            var value = node?[name];
            if (value is null) return string.Empty;
            return value.GetValue<object>()?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ReadInt(JsonNode? node, string name)
    {
        var text = ReadString(node, name);
        return int.TryParse(text, out var value) ? value : 0;
    }

    private static long ReadLong(JsonNode? node, string name)
    {
        var text = ReadString(node, name);
        return long.TryParse(text, out var value) ? value : 0;
    }
}
