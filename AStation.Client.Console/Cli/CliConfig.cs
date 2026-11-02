using System.Text;

namespace AStation_Client.Cli;

/// <summary>
/// 本地配置文件。格式照 BBDown.config 的做法：
/// <list type="bullet">
///   <item>一行一个参数；</item>
///   <item>需要值的选项把值写在下一行（所以值里带空格也不用转义）；</item>
///   <item><c>#</c> 开头的行是注释，空行忽略。</item>
/// </list>
/// 配置里的参数排在命令行参数前面，因此同名选项以命令行为准。
/// </summary>
public static class CliConfig
{
    /// <summary>默认文件名：放在程序目录或当前目录，二者都没有时才认为没配置</summary>
    public const string FileName = "AStation.Client.Console.config";

    /// <summary>指定其它配置文件（BBDown 同款参数名）</summary>
    public const string PathOption = "config-file";

    /// <summary>本次忽略配置文件</summary>
    public const string SkipOption = "no-config";

    /// <summary><c>config init</c> 写出来的模板——全部注释掉，落地后不改变现有行为</summary>
    public const string Template = """
        # AStation.Client.Console 配置文件
        #
        # 格式（同 BBDown.config）：一行一个参数，需要值的选项把值写在下一行，
        # # 开头的行是注释，空行忽略。配置里的参数排在命令行参数之前，
        # 所以命令行上再写一次就会覆盖这里的值。
        #
        # 放在程序目录（exe 旁边）或当前目录即默认生效；也可以用
        # --config-file <路径> 指定别的文件，或用 --no-config 临时跳过。

        # 默认输出 JSON（可直接接 jq）
        #--json

        # 列表类命令的默认条数
        #--limit
        #20

        # 下载默认清晰度与输出方式
        #--quality
        #720
        #--mp4

        # 不要 H.265 清单
        #--h264
        """;

    /// <summary>读取结果</summary>
    public sealed class Result
    {
        /// <summary>实际使用的配置文件；没有则为 null</summary>
        public string? Path { get; init; }

        /// <summary>配置文件贡献的参数（已展开成「选项 + 值」的 token 序列）</summary>
        public IReadOnlyList<string> Tokens { get; init; } = Array.Empty<string>();

        /// <summary>文件里写得有问题的地方——只提示，不影响其它参数</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public bool Loaded => Path is not null;
    }

    /// <summary>
    /// 按 <paramref name="rawArgs"/> 里的 <c>--config-file</c> / <c>--no-config</c> 找到并解析配置。
    /// <paramref name="flags"/> 是「不带值」的开关集合，用来判断某个选项的下一行是值还是下一个选项。
    /// </summary>
    public static Result Load(IReadOnlyList<string> rawArgs, ISet<string> flags)
    {
        if (HasOption(rawArgs, SkipOption)) return new Result();

        string? explicitPath = ReadPathArg(rawArgs);
        string path;
        if (explicitPath is not null)
        {
            path = System.IO.Path.GetFullPath(explicitPath);
            if (!File.Exists(path))
                throw new CliUsageException($"找不到配置文件：{path}");
        }
        else
        {
            path = FindDefault() ?? string.Empty;
            if (path.Length == 0) return new Result();
        }

        var warnings = new List<string>();
        var lines = File.ReadAllLines(path);
        var tokens = ReadTokens(lines, flags, warnings);
        return new Result { Path = path, Tokens = tokens, Warnings = warnings };
    }

    /// <summary>默认位置：先程序目录，再当前目录</summary>
    public static string? FindDefault()
    {
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(beside)) return beside;

        var cwd = System.IO.Path.Combine(Directory.GetCurrentDirectory(), FileName);
        if (File.Exists(cwd) && !string.Equals(cwd, beside, StringComparison.OrdinalIgnoreCase)) return cwd;

        return null;
    }

    /// <summary>
    /// 把配置文件逐行读成 token：跳过注释与空行，去掉成对引号，
    /// 并检查「带值的选项」下一行确实是值（不然后面的裸值会变成位置参数，错得很隐蔽）。
    /// </summary>
    public static List<string> ReadTokens(IEnumerable<string> lines, ISet<string> flags, ICollection<string> warnings)
    {
        var raw = new List<string>();
        int lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            var text = line.Trim();
            if (text.Length == 0) continue;
            if (text[0] == '#' || text.StartsWith("//", StringComparison.Ordinal)) continue;
            raw.Add(Unquote(text));
        }

        var tokens = new List<string>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            string token = raw[i];

            if (!IsOption(token))
            {
                // 正常的「值」在上一轮就被吃掉了，剩下的裸值只能是写错了
                warnings.Add($"配置里的裸值「{token}」被忽略：配置文件只放选项和它的值");
                continue;
            }

            tokens.Add(token);

            string name = token.TrimStart('-');
            if (name.Contains('=')) continue;              // --key=value 自带值
            if (flags.Contains(name)) continue;            // 开关，没有值

            if (i + 1 >= raw.Count)
            {
                warnings.Add($"配置里的选项「{token}」后面没有值");
                continue;
            }

            if (IsOption(raw[i + 1]))
            {
                warnings.Add($"配置里的选项「{token}」后面没有值（下一行是选项「{raw[i + 1]}」）");
                continue;
            }

            tokens.Add(raw[++i]);
        }

        return tokens;
    }

    /// <summary>写一份全注释的模板；目标已存在且没给 <paramref name="overwrite"/> 就不动它</summary>
    public static bool WriteTemplate(string path, bool overwrite, out string message)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (File.Exists(full) && !overwrite)
        {
            message = $"已存在，未覆盖：{full}（加 --force 覆盖）";
            return false;
        }

        var dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 不用 UTF8Encoding(true)：带 BOM 的配置文件会把第一行注释变成「\uFEFF#」
        File.WriteAllText(full, Template + Environment.NewLine, new UTF8Encoding(false));
        message = $"已写入：{full}";
        return true;
    }

    private static bool IsOption(string token)
        => token.Length > 1 && token[0] == '-';

    private static string Unquote(string text)
        => text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private static bool HasOption(IReadOnlyList<string> args, string name)
    {
        foreach (var arg in args)
        {
            if (!IsOption(arg)) continue;
            var bare = arg.TrimStart('-');
            int eq = bare.IndexOf('=');
            if (string.Equals(eq >= 0 ? bare[..eq] : bare, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? ReadPathArg(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!IsOption(args[i])) continue;

            var bare = args[i].TrimStart('-');
            int eq = bare.IndexOf('=');
            if (eq >= 0)
            {
                if (string.Equals(bare[..eq], PathOption, StringComparison.OrdinalIgnoreCase))
                    return bare[(eq + 1)..];
                continue;
            }

            if (string.Equals(bare, PathOption, StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Count && !IsOption(args[i + 1]))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
