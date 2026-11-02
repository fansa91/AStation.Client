using AStation_Client.Cli;

namespace AStation_Client.Tests;

/// <summary>
/// 配置文件解析（格式同 BBDown.config）：一行一个参数，值写在选项的下一行。
/// 这里只测纯函数，不碰真实配置文件。
/// </summary>
public class CliConfigTest
{
    private static readonly string[] SwitchList = { "json", "raw", "mp4", "h264", "no-config", "force", "f", "help" };
    private static readonly HashSet<string> Switches = new(SwitchList, StringComparer.OrdinalIgnoreCase);

    private static List<string> Parse(string text, out List<string> warnings)
    {
        warnings = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return CliConfig.ReadTokens(lines, Switches, warnings);
    }

    [Fact]
    public void 选项和值分两行()
    {
        var tokens = Parse("--quality\n720", out var warnings);

        Assert.Equal(new[] { "--quality", "720" }, tokens);
        Assert.Empty(warnings);
    }

    [Fact]
    public void 注释与空行被忽略()
    {
        var tokens = Parse("# 注释\n\n   \n--json\n// 这也算注释", out var warnings);

        Assert.Equal(new[] { "--json" }, tokens);
        Assert.Empty(warnings);
    }

    [Fact]
    public void 值里可以带空格不用引号()
    {
        // BBDown 就是这个思路：一行整体作为一个参数，所以空格不需要转义
        var tokens = Parse("--keyword\n猫 和 狗", out var warnings);

        Assert.Equal(new[] { "--keyword", "猫 和 狗" }, tokens);
        Assert.Empty(warnings);
    }

    [Fact]
    public void 成对引号会被去掉()
    {
        var tokens = Parse("--out\n\"C:\\我的 下载\\a.ts\"", out _);

        Assert.Equal(new[] { "--out", @"C:\我的 下载\a.ts" }, tokens);
    }

    [Fact]
    public void 等号形式原样保留()
    {
        var tokens = Parse("--quality=720", out var warnings);

        Assert.Equal(new[] { "--quality=720" }, tokens);
        Assert.Empty(warnings);
    }

    [Fact]
    public void 开关不会吃掉下一行的选项()
    {
        // --json 是开关，下一行是新选项而不是它的值
        var tokens = Parse("--json\n--limit\n5", out var warnings);

        Assert.Equal(new[] { "--json", "--limit", "5" }, tokens);
        Assert.Empty(warnings);
    }

    [Fact]
    public void 裸值被忽略并提示()
    {
        // 配置里只放选项；剩下的裸值会变成位置参数，把命令自己的参数挤歪
        var tokens = Parse("--limit\n5\n裸值", out var warnings);

        Assert.Equal(new[] { "--limit", "5" }, tokens);
        Assert.Single(warnings);
        Assert.Contains("裸值", warnings[0]);
    }

    [Fact]
    public void 选项缺值会提示且不影响后面的项()
    {
        var tokens = Parse("--limit\n--json", out var warnings);

        Assert.Equal(new[] { "--limit", "--json" }, tokens);
        Assert.Single(warnings);
        Assert.Contains("--limit", warnings[0]);
    }

    [Fact]
    public void 末行选项缺值会提示()
    {
        var tokens = Parse("--json\n--quality", out var warnings);

        Assert.Equal(new[] { "--json", "--quality" }, tokens);
        Assert.Single(warnings);
        Assert.Contains("--quality", warnings[0]);
    }

    [Fact]
    public void 可以一次读多组()
    {
        var tokens = Parse("""
            # 我的配置
            --limit
            10
            --mp4
            --quality
            1080P
            """, out var warnings);

        Assert.Equal(new[] { "--limit", "10", "--mp4", "--quality", "1080P" }, tokens);
        Assert.Empty(warnings);
    }

    [Fact]
    public void 指定不存在的配置文件会报用法错误()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"astation-no-such-{Guid.NewGuid():N}.config");

        var ex = Assert.Throws<CliUsageException>(() =>
            CliConfig.Load(new[] { "search", "猫", "--config-file", missing }, Switches));

        Assert.Contains("找不到配置文件", ex.Message);
    }

    [Fact]
    public void no_config时完全不读文件()
    {
        var result = CliConfig.Load(new[] { "search", "猫", "--no-config" }, Switches);

        Assert.False(result.Loaded);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void 指定路径读取并展开参数()
    {
        var path = Path.Combine(Path.GetTempPath(), $"astation-cfg-{Guid.NewGuid():N}.config");
        File.WriteAllText(path, "# 测试\n--limit\n3\n");

        try
        {
            var result = CliConfig.Load(new[] { "search", "猫", "--config-file", path }, Switches);

            Assert.True(result.Loaded);
            Assert.Equal(Path.GetFullPath(path), result.Path);
            Assert.Equal(new[] { "--limit", "3" }, result.Tokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 模板写出来是全注释的()
    {
        var path = Path.Combine(Path.GetTempPath(), $"astation-init-{Guid.NewGuid():N}.config");
        try
        {
            Assert.True(CliConfig.WriteTemplate(path, overwrite: false, out _));

            var tokens = CliConfig.ReadTokens(File.ReadAllLines(path), Switches, new List<string>());
            Assert.Empty(tokens);

            // 已存在时不覆盖
            Assert.False(CliConfig.WriteTemplate(path, overwrite: false, out var message));
            Assert.Contains("未覆盖", message);
            Assert.True(CliConfig.WriteTemplate(path, overwrite: true, out _));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
