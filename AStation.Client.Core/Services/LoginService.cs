using System.Text.Json.Serialization;

namespace AStation_Client.Services;

/// <summary>
/// 登录服务。
/// AcFun 写操作（评论、点赞、关注等）依赖浏览器 Cookie 会话，而非独立 AppKey/Token。
/// 支持两种登录方式：
///   1. 扫码登录：/rest/pc-direct/qr/start -> 轮询 /rest/pc-direct/qr/scanResult -> /rest/pc-direct/qr/acceptResult
///   2. 账号密码：POST https://id.app.acfun.cn/rest/web/login/signin (需图形验证码)
/// </summary>
public class LoginService
{
    private readonly ApiService _api;

    public LoginService(ApiService api)
    {
        _api = api;
    }

    internal ApiService GetApiForTest() => _api;

    #region 扫码登录

    /// <summary>
    /// 开始扫码，返回二维码 base64 图像和 token。
    /// 接口：GET https://scan.acfun.cn/rest/pc-direct/qr/start?type=WEB_LOGIN
    /// 注意：扫码接口在 scan.acfun.cn，不是 www.acfun.cn（用错域名会 404）
    /// </summary>
    public async Task<QrStartResponse?> QrStartAsync(CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["type"] = "WEB_LOGIN" };
        return await _api.GetScanAsync<QrStartResponse>("/rest/pc-direct/qr/start", query, ct);
    }

    /// <summary>
    /// 轮询扫码结果。
    /// 接口：GET https://scan.acfun.cn/rest/pc-direct/qr/scanResult?qrLoginToken=...&qrLoginSignature=...
    /// result=0 已扫码；100400002 已过期；10/21 为签名或客户端校验失败（签名与客户端 IP 绑定）
    /// </summary>
    public async Task<QrScanResultResponse?> QrScanResultAsync(string qrLoginToken, string qrLoginSignature, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["qrLoginToken"] = qrLoginToken,
            ["qrLoginSignature"] = qrLoginSignature
        };
        return await _api.GetScanAsync<QrScanResultResponse>("/rest/pc-direct/qr/scanResult", query, ct);
    }

    /// <summary>
    /// 确认扫码并换取登录 Cookie。
    /// 接口：GET https://scan.acfun.cn/rest/pc-direct/qr/acceptResult?qrLoginToken=...&qrLoginSignature=...
    /// </summary>
    public async Task<AcFunResponse<object>?> QrAcceptResultAsync(string qrLoginToken, string qrLoginSignature, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["qrLoginToken"] = qrLoginToken,
            ["qrLoginSignature"] = qrLoginSignature
        };
        return await _api.GetScanAsync<AcFunResponse<object>>("/rest/pc-direct/qr/acceptResult", query, ct);
    }

    #endregion

    #region 账号密码登录

    /// <summary>
    /// 获取图形验证码。
    /// 接口：GET https://id.app.acfun.cn/rest/web/login/captcha
    /// 返回 base64 图像和 key。
    /// </summary>
    public async Task<CaptchaResponse?> GetCaptchaAsync(CancellationToken ct = default)
    {
        return await _api.GetIdAsync<CaptchaResponse>("/rest/web/login/captcha", ct);
    }

    /// <summary>
    /// 账号密码登录。
    /// 接口：POST https://id.app.acfun.cn/rest/web/login/signin
    /// 参数：username, password, key, captcha；需 withCredentials（Cookie 会话）
    /// </summary>
    public async Task<SignInResponse?> SignInAsync(string username, string password, string captchaKey, string captchaCode, CancellationToken ct = default)
    {
        var formData = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["key"] = captchaKey,
            ["captcha"] = captchaCode
        };
        return await _api.PostIdAsync<SignInResponse>("/rest/web/login/signin", formData, ct);
    }

    #endregion

    /// <summary>
    /// 拉取当前登录账号的资料（昵称 / 头像 / userId）。
    /// 接口：POST https://www.acfun.cn/rest/pc-direct/user/personalInfo（无参数，靠 Cookie）
    /// 未登录返回 result=-401 + HTTP 401，此时 <see cref="ProfileResponse.Info"/> 为 null。
    /// </summary>
    public async Task<ProfileResponse?> GetProfileAsync(CancellationToken ct = default)
    {
        return await _api.PostFormAllowErrorAsync<ProfileResponse>("/rest/pc-direct/user/personalInfo", new(), ct);
    }

    /// <summary>
    /// 检查当前 Cookie 会话是否仍然有效（靠 personalInfo 探一次）。
    /// </summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        try
        {
            var profile = await GetProfileAsync(ct);
            return profile?.Result == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 扫码开始响应
/// </summary>
public class QrStartResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("expireTime")]
    public long ExpireTime { get; set; }

    [JsonPropertyName("qrLoginToken")]
    public string? QrLoginToken { get; set; }

    [JsonPropertyName("qrLoginSignature")]
    public string? QrLoginSignature { get; set; }

    [JsonPropertyName("imageData")]
    public string? ImageData { get; set; } // base64 PNG 二维码
}

/// <summary>
/// 扫码轮询响应
/// </summary>
public class QrScanResultResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("qrLoginToken")]
    public string? QrLoginToken { get; set; }
}

/// <summary>
/// 图形验证码响应
/// </summary>
public class CaptchaResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; } // data:image/png;base64,...

    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

/// <summary>
/// 账号密码登录响应。成功时还会带 userId / username / img（头像），
/// 登录页直接用这些填账号区，而不是回显用户输入的手机号。
/// </summary>
public class SignInResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("error_msg")]
    public string? ErrorMsg { get; set; }

    [JsonPropertyName("userId")]
    public long UserId { get; set; }

    [JsonPropertyName("username")]
    public string? UserName { get; set; }

    /// <summary>头像地址</summary>
    [JsonPropertyName("img")]
    public string? Avatar { get; set; }
}

/// <summary>
/// 账号资料响应（personalInfo）
/// </summary>
public class ProfileResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("error_msg")]
    public string? ErrorMsg { get; set; }

    [JsonPropertyName("info")]
    public ProfileInfo? Info { get; set; }
}

/// <summary>
/// personalInfo 的 info 节点（只挑界面用得到的字段）
/// </summary>
public class ProfileInfo
{
    [JsonPropertyName("userId")]
    public long UserId { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    /// <summary>头像地址</summary>
    [JsonPropertyName("headUrl")]
    public string? HeadUrl { get; set; }

    /// <summary>脱敏手机号，如 178****0287</summary>
    [JsonPropertyName("mobile")]
    public string? Mobile { get; set; }
}

/// <summary>
/// 客户端内部登录状态
/// </summary>
public class LoginState
{
    public bool IsLoggedIn { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? AvatarUrl { get; set; }
}
