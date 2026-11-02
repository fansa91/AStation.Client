using AStation_Client.Services;

namespace AStation_Client.Cli.Commands;

/// <summary>
/// <c>livedanmaku &lt;authorId&gt;</c>：连上直播间弹幕通道，把收到的弹幕打出来。
///
/// 弹幕走的是快手 zt.live 的二进制长连接（TCP slink.gifshow.com:14000），
/// 没有 HTTP 接口，所以这里靠 AcFunDanmu 那个库做协议层，命令本身只负责
/// 「连上 → 打印 N 秒 → 断开」，顺便当成界面侧通道的冒烟测试。
///
/// 例：
///   livedanmaku 23682490 --seconds 20
///   livedanmaku 23682490 --json --seconds 10 &gt; danmaku.json
/// </summary>
public static class LiveDanmakuCommand
{
    public static async Task<int> RunAsync(CliArgs args)
    {
        string uid = args.Require(0, "主播 uid（live list 里的 authorId，不是 liveId）");
        if (!long.TryParse(uid, out var authorId) || authorId <= 0)
        {
            throw new CliUsageException($"authorId 要填数字：{uid}");
        }

        int seconds = args.GetInt("seconds", 20);
        bool json = args.Has("json");

        using var service = new LiveDanmakuService(ServiceLocator.Api)
        {
            Logger = message => Out.Diag(message)
        };

        var received = new List<LiveDanmakuMessage>();
        var gate = new object();

        service.MessageReceived += (_, message) =>
        {
            lock (gate) received.Add(message);

            if (json) return;   // --json 走最后统一输出，stdout 必须干净

            Out.Line($"{message.UserName}：{message.Content}");
        };

        service.StatusChanged += (_, status) =>
        {
            switch (status)
            {
                case LiveDanmakuStatus.Connecting:
                    Out.Diag($"连接直播间 {authorId} 的弹幕通道…");
                    break;
                case LiveDanmakuStatus.Closed:
                    Out.Diag("弹幕通道已断开");
                    break;
                case LiveDanmakuStatus.Failed:
                    Out.Diag("弹幕通道连接失败");
                    break;
            }
        };

        Out.Dim($"监听 {seconds} 秒，Ctrl+C 提前结束");
        service.Start(authorId);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), CliContext.Token);
        }
        catch (OperationCanceledException)
        {
            Out.Diag("已中断");
        }

        service.Stop();

        List<LiveDanmakuMessage> snapshot;
        lock (gate) snapshot = new List<LiveDanmakuMessage>(received);

        if (json)
        {
            JsonOut.Write(new
            {
                authorId,
                seconds,
                count = snapshot.Count,
                messages = snapshot.Select(m => new
                {
                    userId = m.UserId,
                    userName = m.UserName,
                    content = m.Content,
                    sendTimeMs = m.SendTimeMs
                })
            });
            return snapshot.Count > 0 ? 0 : 1;
        }

        Out.Line();
        if (snapshot.Count == 0)
        {
            Out.Warn("这段时间没收到弹幕（可能没人说话，或直播已结束）");
            return 1;
        }

        Out.Ok($"共收到 {snapshot.Count} 条弹幕 / {seconds} 秒");
        return 0;
    }
}
