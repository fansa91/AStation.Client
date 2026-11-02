param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\collect"
)

# 收藏分页验收：靠 ASTATION_SEED_PREVIEW=1 灌假数据，看列表排版与分类切换
# 只用于本地截图，不参与发布构建。

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Uy2 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
}
"@
try { [Uy2]::SetProcessDpiAwareness(2) | Out-Null } catch { }
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$env:ASTATION_SEED_PREVIEW = '1'
Start-Process -FilePath (Join-Path $ExeDir 'AStation.Client.exe') -WorkingDirectory $ExeDir
$p = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if (-not $p) { throw 'app did not start' }
Start-Sleep -Seconds 6

# 固定窗口位置与尺寸，否则窗口可能超出屏幕，右侧内容会被裁掉
[Uy2]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 60, 40, 1400, 900, 0x0040) | Out-Null
Start-Sleep -Seconds 2

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

function All { $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }

function Find-El([string]$name) {
    foreach ($e in All) { if ($e.Current.Name -eq $name) { return $e } }
    return $null
}

function Wait-El([string]$name, [int]$seconds = 10) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $e = Find-El $name
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Invoke-El($el) {
    if (-not $el.Current.IsEnabled) { return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle(); return $true }
    $r = $el.Current.BoundingRectangle
    [Uy2]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [Uy2]::mouse_event(2,0,0,0,0); Start-Sleep -Milliseconds 70; [Uy2]::mouse_event(4,0,0,0,0)
    return $true
}

function Save-Screen([string]$name) {
    $hwnd = (Get-Process AStation.Client | Select-Object -First 1).MainWindowHandle
    [Uy2]::SetWindowPos($hwnd, [Uy2]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    [Uy2]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($b.Width, $b.Height)))
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [Uy2]::SetWindowPos($hwnd, [Uy2]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    Write-Output "shot: $name"
}

$account = Find-El '账号'
if (-not $account) { throw '没找到账号按钮' }
Invoke-El $account
Start-Sleep -Seconds 3

$collect = Wait-El '收藏' 8
if (-not $collect) { throw '没找到收藏分页' }
Write-Output "收藏分页: $($collect.Current.ControlType.ProgrammaticName)"
Save-Screen '01-collect-douga'

foreach ($name in @('收藏番剧', '收藏专辑', '收藏文章')) {
    $chip = Find-El $name
    if ($chip) {
        Invoke-El $chip
        Start-Sleep -Seconds 2
        Save-Screen "02-$($name.Replace('收藏',''))"
    } else { Write-Output "WARN: 没找到 $name" }
}

# 切回视频，确认选中态跟着回来
$back = Find-El '收藏视频'
if ($back) { Invoke-El $back; Start-Sleep -Seconds 2; Save-Screen '03-back-to-douga' }

# 播放历史分页（假数据）
$hist = Find-El '播放历史'
if ($hist) { Invoke-El $hist; Start-Sleep -Seconds 2; Save-Screen '04-history' }

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\ASTATION_SEED_PREVIEW -ErrorAction SilentlyContinue
Write-Output 'done'
