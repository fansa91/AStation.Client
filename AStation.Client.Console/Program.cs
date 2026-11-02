using System.Text;
using AStation_Client.Cli;
using AStation_Client.Cli.Commands;

try
{
    Console.OutputEncoding = new UTF8Encoding(false);
}
catch
{
    // 输出被重定向到奇怪的地方时设置编码会抛，忽略即可
}

if (args.Length == 0 && CliConfig.FindDefault() is null)
{
    PrintHelp();
    return 0;
}

if (args.Length > 0 && args[0] is "help" or "-h" or "--help" or "/?")
{
    PrintHelp();
    return 0;
}

// 所有命令共用的开关：解析器需要知道哪些是「不带值」的开关
string[] switchNames = { "json", "raw", "mp4", "h264", "no-config", "force", "f", "help" };
var switches = new HashSet<string>(switchNames, StringComparer.OrdinalIgnoreCase);

// 本地配置文件（格式同 BBDown.config）：里面的参数排在命令行前面，命令行可覆盖
CliConfig.Result config;
try
{
    config = CliConfig.Load(args, switches);
}
catch (CliUsageException ex)
{
    Out.Error(ex.Message);
    return 2;
}

if (config.Loaded)
{
    // 走 stderr：--json 时 stdout 要留给管道
    Out.Diag($"配置文件：{config.Path}（{config.Tokens.Count} 项）");
    foreach (var warning in config.Warnings) Out.Diag($"配置警告：{warning}");
}

if (args.Length == 0)
{
    // 只有配置文件、没给命令 —— 不知道要做什么
    Out.Warn("配置文件已加载，但没有指定命令");
    PrintHelp();
    return 2;
}

string command = args[0].ToLowerInvariant();
string[] rest = args.Skip(1).ToArray();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
CliContext.Token = cts.Token;

var cli = new CliArgs(config.Tokens.Concat(rest), switchNames);

try
{
    return command switch
    {
        "search" or "s" => await ContentCommands.SearchAsync(cli),
        "rank" or "r" => await ContentCommands.RankAsync(cli),
        "video" or "v" => await ContentCommands.VideoAsync(cli),
        "comments" or "c" => await ContentCommands.CommentsAsync(cli),
        "danmaku" or "d" => await ContentCommands.DanmakuAsync(cli),
        "ubb" => await ContentCommands.UbbAsync(cli),
        "bangumi" or "b" => await MediaCommands.BangumiAsync(cli),
        "live" or "l" => await MediaCommands.LiveAsync(cli),
        "play" or "p" => await MediaCommands.PlayAsync(cli),
        "emotes" or "e" => await MediaCommands.EmotesAsync(cli),
        "download" or "dl" => await DownloadCommand.RunAsync(cli),
        "login" or "signin" => await LoginCommand.LoginAsync(cli),
        "whoami" or "me" => await LoginCommand.WhoAmIAsync(cli),
        // config 只看命令行本身，不要把配置里的参数混进来
        "config" or "cfg" => ConfigCommand.Run(new CliArgs(rest, switchNames), config),
        _ => Unknown(command),
    };
}
catch (CliUsageException ex)
{
    Out.Error(ex.Message);
    Out.Dim($"用法：dotnet run --project AStation.Client.Console -- {command} --help");
    return 2;
}
catch (OperationCanceledException)
{
    Out.Warn("已取消");
    return 130;
}
catch (HttpRequestException ex)
{
    Out.Error($"网络请求失败：{ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Out.Error($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

int Unknown(string name)
{
    Out.Error($"没有这个命令：{name}");
    PrintHelp();
    return 2;
}

void PrintHelp()
{
    Out.Line("AStation.Client.Console — AcFun 接口命令行工具（同时是 Core 的 API 使用示例）");
    Out.Line();
    Out.Line("用法：AStation.Client.Console <命令> [参数] [--json]");
    Out.Line();
    Out.Line("内容");
    Out.Line("  search <关键词>      [--sort 1|2|5] [--limit 20]        搜索视频（接口只给前 30 条）");
    Out.Line("  rank                 [--channel 0] [--period today|3d|week] [--limit 20]");
    Out.Line("  video <acId>                                           视频详情 + 分P + 互动数据");
    Out.Line("  comments <acId>      [--page 1] [--type 3] [--limit 10] [--raw]");
    Out.Line("  danmaku <videoId>    [--limit 20] [--raw]               弹幕（videoId 是分P 的 id，不是 acId）");
    Out.Line("  ubb \"<UBB 文本>\"                                       看 UBB 解析成什么（表情 / @ / 图片）");
    Out.Line();
    Out.Line("番剧 / 直播");
    Out.Line("  bangumi list         [--limit 20]                       番剧库");
    Out.Line("  bangumi <bangumiId>                                     番剧详情 + 剧集");
    Out.Line("  live list            [--channel 0] [--limit 20]         直播间列表（0 全部 / 1 游戏 / 2 其他 / 3 娱乐 / 4 虚拟偶像）");
    Out.Line("  live play <authorId> <liveId>                           直播拉流档位");
    Out.Line();
    Out.Line("播放 / 下载");
    Out.Line("  play <resourceId>    [--type 2] [--video id] [--mkey k] 取流：清晰度 + CDN 线路");
    Out.Line("  download <resourceId> [--quality 1080P] [-o out.ts] [--mp4]");
    Out.Line("  download --url <m3u8> [-o out.ts]                       直接下清单");
    Out.Line();
    Out.Line("其它");
    Out.Line("  emotes               [--keyword 笑] [--limit 30]        表情字典");
    Out.Line("  login                --username <手机号/邮箱> --password <密码> [--captcha-code 9gNb]");
    Out.Line("                       [--captcha-image 路径] [--cookies 路径]  账号密码登录（验证码区分大小写）");
    Out.Line("  whoami               [--cookies 路径]                   看当前 Cookie 登录态");
    Out.Line("  config [show|path|init]                                 看 / 建本地配置文件（格式同 BBDown.config）");
    Out.Line();
    Out.Line("通用开关：--json 输出原始结构（可接管道），--h264 不要 H.265 清单，--raw 显示未解析正文");
    Out.Line("配置相关：--config-file <路径> 指定配置文件，--no-config 本次忽略配置");
    Out.Line();
    Out.Line("配置文件：程序目录或当前目录的 AStation.Client.Console.config（config init 生成模板）");
    Out.Line("  一行一个参数，需要值的选项把值写在下一行，# 开头是注释；配置里的参数排在命令行之前");
    Out.Line();
    Out.Line("例子");
    Out.Line("  dotnet run --project AStation.Client.Console -- search 猫 --sort 2 --limit 10");
    Out.Line("  dotnet run --project AStation.Client.Console -- video 12345678 --json");
    Out.Line("  dotnet run --project AStation.Client.Console -- download 12345678 --quality 720 -o 猫.ts");
    Out.Line("  AStation.Client.Console.exe config init");
    Out.Line("  AStation.Client.Console.exe login --username 13800000000 --password *** && whoami");
}
