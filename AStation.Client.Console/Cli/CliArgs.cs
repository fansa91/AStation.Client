namespace AStation_Client.Cli;

/// <summary>
/// 极简参数解析：位置参数 + <c>--key value</c> / <c>--key=value</c>。
/// 需要区分「带值选项」和「开关」——否则 <c>--json</c> 后面的位置参数会被当成它的值。
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags;

    public CliArgs(IEnumerable<string> args, params string[] flags)
    {
        _flags = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);

        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string token = list[i];
            if (token.Length == 0) continue;

            if (token.StartsWith("--", StringComparison.Ordinal) || (token.Length > 1 && token[0] == '-'))
            {
                string name = token.TrimStart('-');
                string? value = null;

                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (!_flags.Contains(name) && i + 1 < list.Count && !list[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    value = list[++i];
                }

                _options[name] = value;
                continue;
            }

            Positional.Add(token);
        }
    }

    /// <summary>位置参数（命令名后面的裸值）</summary>
    public List<string> Positional { get; } = new();

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public string? Get(params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (_options.TryGetValue(alias, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public int GetInt(string name, int fallback)
        => int.TryParse(Get(name), out int value) ? value : fallback;

    public double GetDouble(string name, double fallback)
        => double.TryParse(Get(name), out double value) ? value : fallback;

    /// <summary>第 index 个位置参数，缺失时抛 <see cref="CliUsageException"/></summary>
    public string Require(int index, string what)
    {
        if (index < Positional.Count && !string.IsNullOrWhiteSpace(Positional[index])) return Positional[index];
        throw new CliUsageException($"缺少参数：{what}");
    }

    public string? Optional(int index)
        => index < Positional.Count && !string.IsNullOrWhiteSpace(Positional[index]) ? Positional[index] : null;
}

/// <summary>参数用法错误——打印用法后以退出码 2 结束，不当成崩溃</summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}
