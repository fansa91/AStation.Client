using System.Text.Json;
using System.Text.Json.Serialization;

namespace AStation_Client.Services;

/// <summary>
/// 客户端设置（持久化到 %LOCALAPPDATA%\AStation.Client\settings.json）。
/// 纯 .NET 实现，不依赖 WinUI，方便单测与复用。
/// </summary>
public class AppSettings
{
    private static readonly object SyncRoot = new();
    private static AppSettings? _current;

    // ── 外观 ──
    /// <summary>System / Light / Dark</summary>
    public string Theme { get; set; } = "System";

    /// <summary>侧边栏是否常驻展开</summary>
    public bool ExpandSidebar { get; set; } = true;

    // ── 播放 ──
    /// <summary>首选清晰度：Auto / 1080P / 720P / 540P / 360P</summary>
    public string PreferredQuality { get; set; } = "Auto";

    /// <summary>优先 HEVC（H265）解码流</summary>
    public bool PreferHevc { get; set; } = true;

    /// <summary>进入播放页自动开始播放</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>记住上次播放进度（续播）</summary>
    public bool ResumePlayback { get; set; } = true;

    // ── 弹幕 ──
    public bool DanmakuEnabled { get; set; } = true;
    public double DanmakuOpacity { get; set; } = 0.9;

    /// <summary>弹幕字号基准（最小 16，最大 36）</summary>
    public double DanmakuFontSize { get; set; } = 24;

    /// <summary>弹幕横穿屏幕所用秒数（越小越快）</summary>
    public double DanmakuSpeedSeconds { get; set; } = 9;

    /// <summary>弹幕显示区域占画面比例（0.25 / 0.5 / 0.75 / 1）</summary>
    public double DanmakuArea { get; set; } = 0.75;

    // ── 直播 ──
    /// <summary>直播优先码率档（kbps），0 表示自动取最高</summary>
    public int LivePreferredBitrate { get; set; }

    /// <summary>直播用 FFmpegInteropX 硬件解码（关闭则纯软解，更稳但更吃 CPU）</summary>
    public bool LiveHardwareDecode { get; set; } = true;

    // ── 网络 ──
    public bool PreferBackupCdn { get; set; }

    /// <summary>首页 / 列表每次加载条数</summary>
    public int PageSize { get; set; } = 20;

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStation.Client",
        "settings.json");

    /// <summary>当前设置（首次访问时从磁盘读取）</summary>
    public static AppSettings Current
    {
        get
        {
            lock (SyncRoot)
            {
                return _current ??= Load();
            }
        }
    }

    internal static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // 配置损坏时用默认值，不让设置把应用带崩
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 写盘失败不影响运行
        }
    }

    /// <summary>恢复默认（设置页「重置」用）</summary>
    public void Reset()
    {
        var defaults = new AppSettings();
        Theme = defaults.Theme;
        ExpandSidebar = defaults.ExpandSidebar;
        PreferredQuality = defaults.PreferredQuality;
        PreferHevc = defaults.PreferHevc;
        AutoPlay = defaults.AutoPlay;
        ResumePlayback = defaults.ResumePlayback;
        DanmakuEnabled = defaults.DanmakuEnabled;
        DanmakuOpacity = defaults.DanmakuOpacity;
        DanmakuFontSize = defaults.DanmakuFontSize;
        DanmakuSpeedSeconds = defaults.DanmakuSpeedSeconds;
        DanmakuArea = defaults.DanmakuArea;
        LivePreferredBitrate = defaults.LivePreferredBitrate;
        LiveHardwareDecode = defaults.LiveHardwareDecode;
        PreferBackupCdn = defaults.PreferBackupCdn;
        PageSize = defaults.PageSize;
        Save();
    }
}
