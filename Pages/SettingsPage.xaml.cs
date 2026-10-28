using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.IO;
using AStation_Client.Services;

namespace AStation_Client.Pages;

/// <summary>
/// 设置页（布局参考 Bili.Copilot 的分组卡片 + 行内开关）。
/// 所有选项写进 AppSettings 并立即落盘，返回播放页即刻生效。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly AppSettings _settings = AppSettings.Current;
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        LoadValues();
        _loading = false;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SettingsPathText.Text = AppSettings.SettingsPath;
        VersionText.Text = GetVersion();
    }

    private static string GetVersion()
    {
        try
        {
            var version = typeof(SettingsPage).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "1.0.0";
        }
    }

    /// <summary>把配置写回控件（此时事件被 _loading 挡住，不会触发保存）</summary>
    private void LoadValues()
    {
        ThemeBox.SelectedIndex = _settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };

        ExpandSidebarSwitch.IsOn = _settings.ExpandSidebar;

        QualityBox.SelectedIndex = _settings.PreferredQuality switch
        {
            "1080P" => 1,
            "720P" => 2,
            "540P" => 3,
            "360P" => 4,
            _ => 0
        };

        HevcSwitch.IsOn = _settings.PreferHevc;
        AutoPlaySwitch.IsOn = _settings.AutoPlay;
        ResumeSwitch.IsOn = _settings.ResumePlayback;
        BackupCdnSwitch.IsOn = _settings.PreferBackupCdn;

        DanmakuSwitch.IsOn = _settings.DanmakuEnabled;
        OpacitySlider.Value = Math.Clamp(_settings.DanmakuOpacity, 0.2, 1);
        FontSizeSlider.Value = Math.Clamp(_settings.DanmakuFontSize, 14, 36);
        SpeedSlider.Value = Math.Clamp(_settings.DanmakuSpeedSeconds, 4, 16);
        AreaBox.SelectedIndex = _settings.DanmakuArea switch
        {
            <= 0.3 => 0,
            <= 0.55 => 1,
            <= 0.8 => 2,
            _ => 3
        };

        LiveQualityBox.SelectedIndex = _settings.LivePreferredBitrate switch
        {
            1000 => 1,
            2000 => 2,
            4000 => 3,
            8000 => 4,
            _ => 0
        };
        LiveHwSwitch.IsOn = _settings.LiveHardwareDecode;

        PageSizeSlider.Value = Math.Clamp(_settings.PageSize, 10, 50);

        RefreshValueTexts();
    }

    private void RefreshValueTexts()
    {
        OpacityValueText.Text = $"{OpacitySlider.Value * 100:0}%";
        FontSizeValueText.Text = $"{FontSizeSlider.Value:0}";
        SpeedValueText.Text = $"{SpeedSlider.Value:0.#} 秒横穿";
        PageSizeValueText.Text = $"{PageSizeSlider.Value:0}";
    }

    private void Persist()
    {
        if (_loading) return;
        _settings.Save();
    }

    // ───────────── 外观 ─────────────

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _settings.Theme = ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        };
        Persist();
        ApplyTheme();
    }

    private void ExpandSidebarSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.ExpandSidebar = ExpandSidebarSwitch.IsOn;
        Persist();
        App.MainWindowInstance?.SetSidebarExpanded(_settings.ExpandSidebar);
    }

    /// <summary>立即套用主题，不用重启</summary>
    public static void ApplyTheme()
    {
        if (App.MainWindowInstance?.Content is not FrameworkElement root) return;

        root.RequestedTheme = AppSettings.Current.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    // ───────────── 播放 ─────────────

    private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _settings.PreferredQuality = QualityBox.SelectedIndex switch
        {
            1 => "1080P",
            2 => "720P",
            3 => "540P",
            4 => "360P",
            _ => "Auto"
        };
        Persist();
    }

    private void HevcSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.PreferHevc = HevcSwitch.IsOn;
        Persist();
    }

    private void AutoPlaySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoPlay = AutoPlaySwitch.IsOn;
        Persist();
    }

    private void ResumeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ResumePlayback = ResumeSwitch.IsOn;
        Persist();
    }

    private void BackupCdnSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.PreferBackupCdn = BackupCdnSwitch.IsOn;
        Persist();
    }

    // ───────────── 弹幕 ─────────────

    private void DanmakuSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.DanmakuEnabled = DanmakuSwitch.IsOn;
        Persist();
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.DanmakuOpacity = Math.Round(OpacitySlider.Value, 2);
        OpacityValueText.Text = $"{OpacitySlider.Value * 100:0}%";
        Persist();
    }

    private void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.DanmakuFontSize = FontSizeSlider.Value;
        FontSizeValueText.Text = $"{FontSizeSlider.Value:0}";
        Persist();
    }

    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.DanmakuSpeedSeconds = SpeedSlider.Value;
        SpeedValueText.Text = $"{SpeedSlider.Value:0.#} 秒横穿";
        Persist();
    }

    private void AreaBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _settings.DanmakuArea = AreaBox.SelectedIndex switch
        {
            0 => 0.25,
            1 => 0.5,
            2 => 0.75,
            _ => 1.0
        };
        Persist();
    }

    // ───────────── 直播 ─────────────

    private void LiveQualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _settings.LivePreferredBitrate = LiveQualityBox.SelectedIndex switch
        {
            1 => 1000,
            2 => 2000,
            3 => 4000,
            4 => 8000,
            _ => 0
        };
        Persist();
    }

    private void LiveHwSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LiveHardwareDecode = LiveHwSwitch.IsOn;
        Persist();
    }

    // ───────────── 数据 ─────────────

    private void PageSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.PageSize = (int)PageSizeSlider.Value;
        PageSizeValueText.Text = $"{PageSizeSlider.Value:0}";
        Persist();
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(AppSettings.SettingsPath);
            if (string.IsNullOrEmpty(folder)) return;

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch
        {
            // 打不开资源管理器就算了
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Reset();

        _loading = true;
        LoadValues();
        _loading = false;

        ApplyTheme();
        App.MainWindowInstance?.SetSidebarExpanded(_settings.ExpandSidebar);
    }
}
