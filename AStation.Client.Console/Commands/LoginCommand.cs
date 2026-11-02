using AStation_Client.Services;

namespace AStation_Client.Cli.Commands;

/// <summary>
/// <c>login</c> / <c>whoami</c>：账号密码登录与登录态自检。
///
/// AcFun 的会话是 Cookie，命令行每次都是新进程，所以登录成功后把 Cookie
/// 落到 <c>%LOCALAPPDATA%\AStation.Client\cli-cookies.json</c>，
/// 之后用 <c>whoami</c>（或带 <c>--cookies</c> 的其它命令）复用。
///
/// 例：
///   login --username 13800000000 --password ***          自动取验证码、存图、提示输入
///   login --username ... --password ... --captcha-code 9gNb
///   whoami
/// </summary>
public static class LoginCommand
{
    /// <summary>默认 Cookie 文件位置</summary>
    public static string DefaultCookiePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStation.Client",
        "cli-cookies.json");

    public static async Task<int> LoginAsync(CliArgs args)
    {
        string? username = args.Get("username", "u");
        string? password = args.Get("password", "p");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new CliUsageException("login 需要 --username 和 --password（账号要填手机号或邮箱，A站不接受老式用户名）");
        }

        string cookiePath = args.Get("cookies") ?? DefaultCookiePath;
        bool json = args.Has("json");
        var login = ServiceLocator.Login;

        // ① 图形验证码：正常登录不一定需要，但短期内多次登录就会被要求
        string? captchaCode = args.Get("captcha-code", "captcha");
        string? key = args.Get("captcha-key");

        if (key is not null)
        {
            // 外部已经取过验证码（脚本里配合自己看图），不再重新取一张
            Note(json, "沿用 --captcha-key 指定的验证码");
            if (captchaCode is null) throw new CliUsageException("给了 --captcha-key 就必须同时给 --captcha-code");
        }
        else
        {
            var captcha = await login.GetCaptchaAsync(CliContext.Token);
            if (captcha?.Result == 0 && !string.IsNullOrEmpty(captcha.Key) && !string.IsNullOrEmpty(captcha.Image))
            {
                key = captcha.Key;
                string imagePath = args.Get("captcha-image") ?? DefaultCaptchaPath();

                if (captchaCode is null)
                {
                    // 交互读验证码必须走终端，--json 下这种事没法自动完成
                    if (json) throw new CliUsageException("--json 不能交互输入，请加 --captcha-code（或先取图再 --captcha-key）");

                    WriteCaptchaImage(captcha.Image, imagePath);
                    Out.Dim($"验证码已保存：{imagePath}");
                    Out.Line("打开看图，输入图中的 4 个字符（区分大小写），然后回车：");
                    captchaCode = Console.ReadLine()?.Trim();
                    if (string.IsNullOrWhiteSpace(captchaCode))
                    {
                        Out.Error("没有输入验证码，已取消");
                        return 2;
                    }
                }
            }
            else
            {
                Out.Diag($"验证码接口没给图（result={captcha?.Result}），直接尝试登录");
            }
        }

        Note(json, $"登录中：{username}");

        var result = await login.SignInAsync(username!, password!, key ?? string.Empty, captchaCode ?? string.Empty, CliContext.Token);
        bool ok = result?.Result == 0;

        // ② 成功後把 Cookie 存盘，再用 personalInfo 验一次会话、取昵称头像
        ProfileResponse? profile = null;
        if (ok)
        {
            ServiceLocator.Api.SaveCookies(cookiePath);
            profile = await login.GetProfileAsync(CliContext.Token);
        }

        if (json)
        {
            JsonOut.Write(new
            {
                ok,
                result = result?.Result,
                errorMsg = result?.ErrorMsg,
                userId = profile?.Info?.UserId ?? result?.UserId,
                userName = profile?.Info?.UserName ?? result?.UserName,
                avatar = profile?.Info?.HeadUrl ?? result?.Avatar,
                mobile = profile?.Info?.Mobile,
                profileResult = profile?.Result,
                cookies = ok ? cookiePath : null,
            });
            return ok ? 0 : 1;
        }

        if (!ok)
        {
            Out.Error($"登录失败：{result?.ErrorMsg ?? "未知错误"}（result={result?.Result}）");
            Out.Dim(Explain(result?.Result));
            return 1;
        }

        Out.Ok($"登录成功：{profile?.Info?.UserName ?? result!.UserName ?? username}（uid {profile?.Info?.UserId ?? result.UserId}）");

        if (profile?.Result == 0 && profile.Info is not null)
        {
            Out.KeyValues(
                ("昵称", profile.Info.UserName ?? "-"),
                ("uid", profile.Info.UserId.ToString()),
                ("手机", profile.Info.Mobile ?? "-"),
                ("头像", profile.Info.HeadUrl ?? "-"));
        }
        else
        {
            Out.Warn($"登录接口返回成功，但 personalInfo 拿不到资料（result={profile?.Result}）——Cookie 可能没写进容器");
        }

        Out.Line();
        Out.Dim($"Cookie 已保存：{cookiePath}");
        Out.Dim("之后可用 whoami 验证，或其它命令加 --cookies <路径> 复用登录态");
        return 0;
    }

    /// <summary>给人看的进度提示：--json 时走 stderr，免得污染管道</summary>
    private static void Note(bool json, string text)
    {
        if (json) Out.Diag(text);
        else Out.Dim(text);
    }

    public static async Task<int> WhoAmIAsync(CliArgs args)
    {
        string cookiePath = args.Get("cookies") ?? DefaultCookiePath;
        int loaded = ServiceLocator.Api.LoadCookies(cookiePath);

        var profile = await ServiceLocator.Login.GetProfileAsync(CliContext.Token);
        bool ok = profile?.Result == 0 && profile.Info is not null;

        if (args.Has("json"))
        {
            JsonOut.Write(new
            {
                loggedIn = ok,
                result = profile?.Result,
                errorMsg = profile?.ErrorMsg,
                cookieFile = cookiePath,
                cookiesLoaded = loaded,
                userId = profile?.Info?.UserId,
                userName = profile?.Info?.UserName,
                avatar = profile?.Info?.HeadUrl,
                mobile = profile?.Info?.Mobile,
            });
            return ok ? 0 : 1;
        }

        Out.Section("登录态");
        Out.KeyValues(
            ("Cookie 文件", $"{cookiePath}{(File.Exists(cookiePath) ? string.Empty : "  ← 不存在")}"),
            ("载入条数", loaded.ToString()),
            ("状态", ok ? "已登录" : $"未登录（result={profile?.Result}）"));

        if (ok)
        {
            Out.KeyValues(
                ("昵称", profile!.Info!.UserName ?? "-"),
                ("uid", profile.Info.UserId.ToString()),
                ("手机", profile.Info.Mobile ?? "-"));
            return 0;
        }

        Out.Dim("先跑一次 login 拿 Cookie，再回来看");
        return 1;
    }

    private static string DefaultCaptchaPath() => Path.Combine(Path.GetTempPath(), "astation-captcha.png");

    private static void WriteCaptchaImage(string dataUri, string path)
    {
        string base64 = dataUri.Contains(',') ? dataUri.Split(',', 2)[1] : dataUri;
        var bytes = Convert.FromBase64String(base64);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>把 AcFun 的登录错误码翻成人话</summary>
    private static string Explain(int? code) => code switch
    {
        100001016 => "图片验证码不对：注意区分大小写，看错了就重跑一次换图",
        100001017 => "账号格式不对：A站只认手机号或邮箱，老式用户名登不了",
        100001005 => "账号或密码错（也可能是账号被封）",
        _ => "接口没给出更具体的说明",
    };
}
