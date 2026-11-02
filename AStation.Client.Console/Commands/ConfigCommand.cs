namespace AStation_Client.Cli.Commands;

/// <summary>
/// <c>config</c>：看 / 建本地配置文件（格式同 BBDown.config）。
///
/// 例：
///   config                看当前生效的配置文件与解析出的参数
///   config path           只输出路径（脚本里用）
///   config init           在当前目录写一份全注释模板
///   config init --path C:\Tools\a.config --force
/// </summary>
public static class ConfigCommand
{
    public static int Run(CliArgs args, CliConfig.Result current)
    {
        string mode = (args.Optional(0) ?? "show").ToLowerInvariant();
        return mode switch
        {
            "show" => Show(current),
            "path" => ShowPath(current),
            "init" => Init(args),
            _ => throw new CliUsageException($"config 支持 show / path / init，不认识「{mode}」"),
        };
    }

    private static int Show(CliConfig.Result current)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, CliConfig.FileName);
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), CliConfig.FileName);

        Out.Section("配置文件");
        Out.KeyValues(
            ("格式", "一行一个参数；需要值的选项把值写在下一行；# 开头是注释"),
            ("生效", current.Path ?? "（无）"),
            ("参数", current.Tokens.Count == 0 ? "（无）" : $"{current.Tokens.Count} 项"),
            ("程序目录", $"{beside}{(File.Exists(beside) ? "  ← 存在" : string.Empty)}"),
            ("当前目录", $"{cwd}{(File.Exists(cwd) ? "  ← 存在" : string.Empty)}"));

        if (current.Tokens.Count > 0)
        {
            Out.Line();
            Out.Dim("解析结果（配置在前，命令行在后 → 命令行同名参数优先）：");
            foreach (var token in current.Tokens) Out.Line($"  {token}");
        }

        Out.Line();
        Out.Dim($"优先级：--config-file <路径> ＞ 程序目录 ＞ 当前目录；--no-config 跳过配置");
        Out.Dim($"模板：config init（写到当前目录 {CliConfig.FileName}）");
        return 0;
    }

    private static int ShowPath(CliConfig.Result current)
    {
        if (current.Path is null)
        {
            Out.Diag("没有找到配置文件");
            return 1;
        }
        Out.Line(current.Path);
        return 0;
    }

    private static int Init(CliArgs args)
    {
        string target = args.Get("path", "p")
                        ?? Path.Combine(Directory.GetCurrentDirectory(), CliConfig.FileName);

        bool overwrite = args.Has("force") || args.Has("f");
        bool written = CliConfig.WriteTemplate(target, overwrite, out string message);

        if (written) Out.Ok(message);
        else Out.Warn(message);

        if (written)
        {
            Out.Dim("模板里的选项全是注释掉的，按需删掉行首的 # 即可生效；改完用 config show 检查。");
        }
        return written ? 0 : 1;
    }
}
