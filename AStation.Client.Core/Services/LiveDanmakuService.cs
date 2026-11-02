using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AcFunDanmu;
using AcFunDanmu.Enums;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace AStation_Client.Services;

/// <summary>直播间一条弹幕</summary>
public class LiveDanmakuMessage
{
    public long UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>发送时间（毫秒时间戳，服务端给的）</summary>
    public long SendTimeMs { get; set; }

    /// <summary>true = 进房时下发的历史弹幕（不是刚发的），界面可以只塞列表不飘屏</summary>
    public bool IsRecent { get; set; }

    public override string ToString() => $"{UserName}: {Content}";
}

public enum LiveDanmakuStatus
{
    Connecting,
    Connected,
    Closed,
    Failed
}

/// <summary>
/// 直播间弹幕通道。
///
/// AcFun 直播弹幕是快手 zt.live 的二进制协议（magic 0xABCD0001 + protobuf 头 + AES 加密载荷），
/// 走 TCP 直连 slink.gifshow.com:14000，没有 HTTP 接口。握手/注册/进房/心跳由 NuGet 上的
/// <c>AcFunDanmu</c>（MIT）负责，本类负责取令牌、准备进房参数、断线重连。
///
/// **为什么要自己走一遍初始化**：库的 Initialize 把令牌当标准 Base64 解码、把 acSecurity 当
/// UTF-8 字符串当密钥，两处都和现在（2026-11）服务端的期望不符 —— 现象是先 FormatException、
/// 补成合法 base64 后再被判定 10026「token 过期」，密钥不对则 10012「解密失败」。
/// 正确做法（对照 wscxh1/acfundanmu 的 Go 实现）：
///   令牌 → 服务端要「字符串的 UTF-8 原始字节」，故先 base64 一次骗过库的 FromBase64；
///   密钥 → acSecurity 的 base64 **解码**结果（16 字节 AES-128）。
/// </summary>
public sealed class LiveDanmakuService : IDisposable
{
    private const string KuaishouHost = "https://api.kuaishouzt.com";
    private const int MaxRetry = 3;

    private readonly ApiService _api;
    private readonly HashSet<string> _seen = new();
    private Client? _client;
    private long _authorId;
    private int _retry;
    private bool _stopping;
    private bool _disposed;

    public LiveDanmakuService(ApiService api)
    {
        _api = api;
    }

    /// <summary>收到一条弹幕（在后台读取线程触发，界面要自己切回 UI 线程）</summary>
    public event EventHandler<LiveDanmakuMessage>? MessageReceived;

    /// <summary>连接状态变化（后台线程触发）</summary>
    public event EventHandler<LiveDanmakuStatus>? StatusChanged;

    /// <summary>诊断日志出口（界面接 RuntimeLog，命令行接 stderr）</summary>
    public Action<string>? Logger { get; set; }

    public bool IsRunning => _client?.IsRunning == true;

    public long AuthorId => _authorId;

    private void Log(string message) => Logger?.Invoke(message);

    /// <summary>连接某个主播的直播间（uid = 主播 id，不是 liveId）</summary>
    public void Start(long authorId)
    {
        if (_disposed || authorId <= 0) return;
        if (_client is not null && _authorId == authorId && _client.IsRunning) return;

        Stop(notify: false);

        _authorId = authorId;
        _retry = 0;
        _stopping = false;

        // 内部是 async void 接收循环，这里不 await；失败会走 OnEnd 重连
        _ = ConnectAsync(authorId);
    }

    private async Task ConnectAsync(long authorId)
    {
        try
        {
            StatusChanged?.Invoke(this, LiveDanmakuStatus.Connecting);

            var prepared = await PrepareAsync(authorId);
            if (prepared is null)
            {
                StatusChanged?.Invoke(this, LiveDanmakuStatus.Failed);
                return;
            }

            var (userId, token, securityKey, tickets, attach, liveId) = prepared.Value;

            var client = new Client(userId, token, securityKey, tickets, attach, liveId,
                Logger is null ? null : new ProtocolLogger(Logger));

            // 让库跳过自己的 Initialize：它会拿 URL-safe 令牌去 FromBase64，必炸
            if (!TryPinHostId(client, authorId))
            {
                Log("无法固定 HostId（库版本变了？），弹幕连接可能失败");
            }

            client.Handler += OnHandler;
            client.OnStart += OnClientStart;
            client.OnEnd += OnClientEnd;
            _client = client;

            client.Start(authorId);
        }
        catch (Exception ex)
        {
            Log($"启动弹幕连接失败：{ex.Message}");
            StatusChanged?.Invoke(this, LiveDanmakuStatus.Failed);
        }
    }

    /// <summary>
    /// 取访客令牌 + 进房票据。返回 null 表示这条链路不通（直播结束 / 接口变动）。
    ///
    /// 顺序有讲究：必须先有 <c>_did</c>，否则 visitor/login 直接返回
    /// <c>201646582 请求不合法</c>（服务端把缺设备号当非法请求）。
    /// </summary>
    private async Task<(long UserId, string Token, byte[] SecurityKey, string[] Tickets, string Attach, string LiveId)?>
        PrepareAsync(long authorId)
    {
        var did = await EnsureDeviceIdAsync();

        var login = await _api.PostIdAsync<JsonNode>(
            "/rest/app/visitor/login",
            new Dictionary<string, string> { ["sid"] = "acfun.api.visitor" });

        var userIdText = login?["userId"]?.ToString();
        var rawToken = login?["acfun.api.visitor_st"]?.ToString();
        var security = login?["acSecurity"]?.ToString();

        if (string.IsNullOrWhiteSpace(userIdText) || string.IsNullOrWhiteSpace(rawToken))
        {
            Log($"取访客令牌失败：{login?["error_msg"]?.ToString() ?? "无响应"}");
            return null;
        }

        if (!long.TryParse(userIdText, out var userId)) return null;

        if (string.IsNullOrWhiteSpace(security))
        {
            Log("访客登录没返回 acSecurity，无法加密 Register 包");
            return null;
        }

        // 密钥 = acSecurity 的 **base64 解码**结果（16 字节，AES-128-CBC）。
        // 注意库自己走 Initialize 时用的是 Encoding.UTF8.GetBytes，那样只有 24 字节（AES-192），
        // 服务端解不开，所以这里必须显式传正确的字节。
        byte[] securityKey;
        try
        {
            securityKey = Convert.FromBase64String(security);
        }
        catch
        {
            Log("acSecurity 不是合法 base64，无法加密 Register 包");
            return null;
        }

        var token = EncodeServiceToken(rawToken);

        // 参数名/取值照抄官方 web 端：kpn 是 ACFUN_APP、kpf 是 PC_WEB（写 LIVE_MATE/WINDOWS_PC 会被拒）
        var query = new Dictionary<string, string>
        {
            ["subBiz"] = "mainApp",
            ["kpn"] = "ACFUN_APP",
            ["kpf"] = "PC_WEB",
            ["userId"] = userIdText,
            ["did"] = did,
            ["acfun.api.visitor_st"] = rawToken
        };
        var form = new Dictionary<string, string>
        {
            ["authorId"] = authorId.ToString()
        };

        var play = await _api.PostFormToHostAsync<JsonNode>(
            KuaishouHost, "/rest/zt/live/web/startPlay", form, query);

        var data = play?["data"];
        var tickets = data?["availableTickets"]?.AsArray()
            .Select(node => node?.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToArray();

        var attach = data?["enterRoomAttach"]?.ToString();
        var liveId = data?["liveId"]?.ToString() ?? string.Empty;

        if (tickets is null || tickets.Length == 0 || string.IsNullOrWhiteSpace(attach))
        {
            Log($"取进房票据失败：result={play?["result"]} {play?["error_msg"]}");
            return null;
        }

        return (userId, token, securityKey, tickets, attach, liveId);
    }

    /// <summary>确保拿到 _did（访问直播站首页时由服务端下发）；抓不到就退化成随机 UUID（官方也说可以）</summary>
    private async Task<string> EnsureDeviceIdAsync()
    {
        var did = _api.GetCookieValue(ApiService.LiveSiteHost, "_did");
        if (!string.IsNullOrWhiteSpace(did)) return did!;

        try
        {
            await _api.GetStringFromHostAsync(ApiService.LiveSiteHost, "/", null);
        }
        catch
        {
            // 首页打不开也无所谓，后面用随机值兜底
        }

        did = _api.GetCookieValue(ApiService.LiveSiteHost, "_did");
        return string.IsNullOrWhiteSpace(did) ? Guid.NewGuid().ToString("N") : did!;
    }

    /// <summary>
    /// 把访客令牌转成「喂给库之后，<c>ByteString.FromBase64</c> 正好还原成令牌原始字节」的形式。
    ///
    /// 库的 <c>HandshakeRequest/RegisterRequest</c> 里写的是
    /// <c>Token = ByteString.FromBase64(_serviceToken)</c>，也就是它默认令牌本身是 base64；
    /// 但服务端要的是令牌**字符串的 UTF-8 原始字节**（Go 实现里就是 <c>[]byte(ServiceToken)</c>）。
    /// 所以先 base64 一次，让库再解开时正好得到原始字节。
    /// 直接传原串（无论是否补位）都会在握手阶段被判定 10026「token 过期」。
    /// </summary>
    private static string EncodeServiceToken(string rawToken) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(rawToken));

    /// <summary>把库的 HostId 直接设成主播 id，让 <c>Start</c> 跳过内部 Initialize</summary>
    private static bool TryPinHostId(Client client, long authorId)
    {
        try
        {
            var field = typeof(Client).GetField("<HostId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is null) return false;

            field.SetValue(client, authorId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>库把 TCP 连上并跑通握手后会回调这里（后台线程）</summary>
    private void OnClientStart()
    {
        if (_stopping || _disposed) return;

        _retry = 0;
        Log($"弹幕通道已连接：直播间 {_authorId}");
        StatusChanged?.Invoke(this, LiveDanmakuStatus.Connected);
    }

    private void OnClientEnd()
    {
        if (_stopping || _disposed) return;

        if (_retry < MaxRetry)
        {
            _retry++;
            Log($"弹幕连接断开，第 {_retry} 次重连");
            _ = ConnectAsync(_authorId);
            return;
        }

        Log("弹幕连接多次重试仍失败（直播可能已结束）");
        StatusChanged?.Invoke(this, LiveDanmakuStatus.Closed);
    }

    /// <summary>
    /// 协议层回调。
    ///
    /// 评论有两条来路（实测 2026-11）：
    ///   ACTION_SIGNAL / COMMENT      —— 实时弹幕，一条一条推；
    ///   STATE_SIGNAL / RECENT_COMMENT —— 进房瞬间下发一批「最近评论」当历史。
    /// 其余状态信号（DisplayInfo / TopUsers / RedpackList …）每秒都在刷，全部忽略。
    /// </summary>
    private void OnHandler(Client sender, string messageType, ByteString payload)
    {
        if (messageType == PushMessage.STATE_SIGNAL)
        {
            HandleStateSignal(payload);
            return;
        }

        if (messageType != PushMessage.ACTION_SIGNAL) return;

        try
        {
            var signal = ZtLiveScActionSignal.Parser.ParseFrom(payload);
            foreach (var item in signal.Item)
            {
                if (item.SignalType != PushMessage.ActionSignal.COMMENT) continue;

                foreach (var raw in item.Payload)
                {
                    Emit(CommonActionSignalComment.Parser.ParseFrom(raw), isRecent: false);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"解析实时弹幕失败：{ex.Message}");
        }
    }

    private void HandleStateSignal(ByteString payload)
    {
        try
        {
            var state = ZtLiveScStateSignal.Parser.ParseFrom(payload);
            foreach (var item in state.Item)
            {
                if (item.SignalType != PushMessage.StateSignal.RECENT_COMMENT) continue;
                if (item.Payload.IsEmpty) continue;

                var recent = CommonStateSignalRecentComment.Parser.ParseFrom(item.Payload);
                foreach (var comment in recent.Comment)
                {
                    Emit(comment, isRecent: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"解析历史弹幕失败：{ex.Message}");
        }
    }

    private void Emit(CommonActionSignalComment comment, bool isRecent)
    {
        var content = comment.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content)) return;

        var userId = comment.UserInfo?.UserId ?? 0;

        // 历史那批和实时流会重叠（同一条可能先出现在 RECENT_COMMENT、随后又推一次）→ 去重
        var key = $"{userId}|{comment.SendTimeMs}|{content}";
        if (!_seen.Add(key)) return;

        if (_seen.Count > 2000) _seen.Clear();

        MessageReceived?.Invoke(this, new LiveDanmakuMessage
        {
            UserId = userId,
            UserName = comment.UserInfo?.Nickname ?? string.Empty,
            Content = content,
            SendTimeMs = comment.SendTimeMs,
            IsRecent = isRecent
        });
    }

    /// <summary>断开连接（离开直播间 / 换房间时调用）</summary>
    public void Stop(bool notify = true)
    {
        _stopping = true;

        try
        {
            if (_client is not null)
            {
                _client.Handler -= OnHandler;
                _client.OnStart -= OnClientStart;
                _client.OnEnd -= OnClientEnd;
                _client.Stop("page closed");
                _client = null;
            }
        }
        catch (Exception ex)
        {
            Log($"关闭弹幕连接失败：{ex.Message}");
        }

        if (notify) StatusChanged?.Invoke(this, LiveDanmakuStatus.Closed);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>把 AcFunDanmu 协议层的 ILogger 日志转到我们的诊断出口</summary>
internal sealed class ProtocolLogger : ILogger<Client>
{
    private readonly Action<string> _write;

    public ProtocolLogger(Action<string> write)
    {
        _write = write;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var text = formatter(state, exception);
        if (exception is not null) text += $" ← {exception.GetType().Name}: {exception.Message}";
        _write($"[{logLevel}] {text}");
    }
}
