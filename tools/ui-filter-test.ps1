param([int]$TargetPid = 0)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32FilterTest {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$proc = if ($TargetPid -gt 0) { Get-Process -Id $TargetPid -ErrorAction SilentlyContinue }
        else { Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { throw "AStation.Client is not running" }
$proc.Refresh()
$hwnd = $proc.MainWindowHandle

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

function Shot([string]$name) {
    [Win32FilterTest]::ShowWindow($hwnd, 9) | Out-Null
    [Win32FilterTest]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 600
    $rect = New-Object Win32FilterTest+RECT
    [Win32FilterTest]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $path = Join-Path "C://Users//Administrator//Documents//pro//AStation.Client//artifacts" $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved: $name"
}

function ClickByName([string]$label) {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ($e.Current.Name -ne $label) { continue }
        $p = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
        if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return $true }
        if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
    }
    return $false
}

# 1. 直播页：切到「游戏」分类
ClickByName '直播' | Out-Null
Start-Sleep -Milliseconds 2200
$ok = ClickByName '游戏'
Write-Output "click 游戏 chip: $ok"
Start-Sleep -Milliseconds 2500
Shot '07-live-game.png'

# 2. 排行榜：周榜 + 动画频道
ClickByName '排行榜' | Out-Null
Start-Sleep -Milliseconds 2200
$ok1 = ClickByName '周榜'
Start-Sleep -Milliseconds 2200
$ok2 = ClickByName '动画'
Write-Output "click 周榜: $ok1, 动画: $ok2"
Start-Sleep -Milliseconds 2500
Shot '08-rank-week-anime.png'
