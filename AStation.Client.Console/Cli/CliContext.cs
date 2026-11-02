namespace AStation_Client.Cli;

/// <summary>跨命令共享的运行时状态（Ctrl+C 的取消令牌）</summary>
public static class CliContext
{
    public static CancellationToken Token { get; set; }
}
