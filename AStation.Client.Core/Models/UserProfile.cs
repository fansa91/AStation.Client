namespace AStation_Client.Services;

/// <summary>
/// 用户公开资料。字段取自 GET /rest/pc-direct/user/userInfo?userId= 的 profile。
///
/// profile 有 30 个键，这里只取界面上用得到的：
///   name / userId / headUrl / spaceImage / contentCount（投稿）/
///   likeCount（获赞）/ following（关注）/ followed（粉丝）/
///   verifiedText / registerTime / ipLocation / shareUrl
///
/// 注意计数类字段在 Web 端是**字符串**（"442"），App 端有时是数字，
/// 解析一律走字符串转换，两边都能吃。
/// </summary>
public class UserProfile
{
    public long UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Avatar { get; set; } = string.Empty;

    /// <summary>空间横幅图（可能为空）</summary>
    public string SpaceImage { get; set; } = string.Empty;

    /// <summary>投稿数</summary>
    public int ContentCount { get; set; }

    /// <summary>获赞数</summary>
    public int LikeCount { get; set; }

    /// <summary>关注数</summary>
    public int Following { get; set; }

    /// <summary>粉丝数</summary>
    public int Followed { get; set; }

    /// <summary>认证信息（"AcFun 认证用户" 等），没认证就是空串</summary>
    public string VerifiedText { get; set; } = string.Empty;

    /// <summary>注册时间（毫秒时间戳，Web 端常给 0）</summary>
    public long RegisterTimeMs { get; set; }

    public string IpLocation { get; set; } = string.Empty;

    public string ShareUrl { get; set; } = string.Empty;

    /// <summary>「442 投稿 · 377 获赞 · 3 关注 · 582 粉丝」</summary>
    public string StatsText =>
        $"{MetricFormat.Count(ContentCount)} 投稿 · {MetricFormat.Count(LikeCount)} 获赞 · " +
        $"{MetricFormat.Count(Following)} 关注 · {MetricFormat.Count(Followed)} 粉丝";

    /// <summary>「注册于 2019年10月17日」</summary>
    public string RegisterText
    {
        get
        {
            if (RegisterTimeMs <= 0) return string.Empty;
            try
            {
                var local = DateTimeOffset.FromUnixTimeMilliseconds(RegisterTimeMs).LocalDateTime;
                return $"注册于 {local:yyyy年M月d日}";
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>「uid 16517519 · IP 属地 北京」这种补充行</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (UserId > 0) parts.Add($"uid {UserId}");
            if (!string.IsNullOrWhiteSpace(IpLocation)) parts.Add($"IP 属地 {IpLocation}");
            if (!string.IsNullOrWhiteSpace(RegisterText)) parts.Add(RegisterText);
            return string.Join(" · ", parts);
        }
    }
}
