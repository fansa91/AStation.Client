param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\live-danmaku",
    [string]$Tag = "ld"
)

# 直播弹幕专项探针：首页 → 直播 → 进房间 → 截播放页 → 切「弹幕」分页 → 再截。
# 目的：确认弹幕开关可用、右栏列表真的在长弹幕、状态面板没卡在「未接入」。
# 子进程在本脚本内启动、结束，避免宿主回收。

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Ld {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
"@

try { [Ld]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Ld]::SetProcessDPIAware() | Out-Null } catch { }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-AppProc {
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { $p.Refresh() }
    return $p
}

function Focus-Window($hwnd) {
    for ($i = 0; $i -lt 8; $i++) {
        [Ld]::ShowWindow($hwnd, 9) | Out-Null
        [Ld]::SetWindowPos($hwnd, [Ld]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
        [Ld]::SwitchToThisWindow($hwnd, $true) | Out-Null
        [Ld]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 200
        if ([Ld]::GetForegroundWindow() -eq $hwnd) { break }
    }
}

function Save-Screen([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, (New-Object System.Drawing.Size($b.Width, $b.Height)))
    $g.Dispose()
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "shot: $path"
}

function Get-WindowShot([IntPtr]$hwnd, [string]$name) {
    $r = New-Object Ld+RECT
    [Ld]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "shot: $path (${w}x${h})"
}

function Get-AllElements {
    $proc = Get-AppProc
    if (-not $proc) { return @() }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { return @() }
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
}

function Click-Point([double]$x, [double]$y) {
    [Ld]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 150
    [Ld]::mouse_event([Ld]::LEFTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 70
    [Ld]::mouse_event([Ld]::LEFTUP, 0, 0, 0, 0)
}

function Invoke-El($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return }
    $r = $el.Current.BoundingRectangle
    if ($r.Width -gt 0 -and $r.Height -gt 0) { Click-Point ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) }
}

function Find-El([string]$name) {
    foreach ($e in Get-AllElements) { if ($e.Current.Name -eq $name) { return $e } }
    return $null
}

function Wait-El([string]$name, [int]$seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $e = Find-El $name
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# ───────────────────────── 启动 ─────────────────────────
Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Start-Process -FilePath (Join-Path $ExeDir 'AStation.Client.exe') -WorkingDirectory $ExeDir
$proc = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-AppProc
    if ($proc -and $proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if (-not $proc) { throw 'app did not start' }
Write-Output "app pid=$($proc.Id)"
Start-Sleep -Seconds 6

# ───────────────────────── 进直播列表 ─────────────────────────
$nav = Wait-El '直播' 12
if (-not $nav) { throw '没找到「直播」导航项' }
Invoke-El $nav
Start-Sleep -Seconds 7

# 直播卡片也是 Group，尺寸比视频卡片大一点，按范围放宽筛选
$cards = @()
foreach ($e in Get-AllElements) {
    $ct = $e.Current.ControlType.ProgrammaticName
    if ($ct -notin @('ControlType.Group', 'ControlType.Button', 'ControlType.ListItem')) { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Width -lt 150 -or $r.Width -gt 520) { continue }
    if ($r.Height -lt 110 -or $r.Height -gt 460) { continue }
    if ([string]::IsNullOrWhiteSpace($e.Current.Name)) { continue }
    if ($e.Current.Name -eq '直播') { continue }
    $cards += $e
}
if ($cards.Count -eq 0) { throw '没有可点的直播卡片' }
$card = $cards[0]
$cr = $card.Current.BoundingRectangle
Write-Output "点直播卡片：$($card.Current.Name) @ $($cr.X),$($cr.Y) $($cr.Width)x$($cr.Height)"
Click-Point ($cr.X + $cr.Width / 2) ($cr.Y + $cr.Height / 2)

# ───────────────────────── 等进房 + 等弹幕 ─────────────────────────
$tab = Wait-El '弹幕' 20
if (-not $tab) { throw '没进入直播间页面（找不到「弹幕」分页）' }
Write-Output "进入直播间，等弹幕下发…"
Start-Sleep -Seconds 12

$hwnd = (Get-AppProc).MainWindowHandle
Focus-Window $hwnd
$wr = New-Object Ld+RECT
[Ld]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
[Ld]::SetCursorPos([int](($wr.Left + $wr.Right) / 2), [int](($wr.Top + $wr.Bottom) / 2)) | Out-Null
Start-Sleep -Milliseconds 800
Save-Screen "$Tag-1-room"

# 弹幕开关的可用状态（禁用说明通道还没连上）
$toggle = $null
foreach ($e in Get-AllElements) {
    if ($e.Current.Name -eq '弹幕开关' -or $e.Current.Name -eq '弹幕') {
        $ct = $e.Current.ControlType.ProgrammaticName
        if ($ct -eq 'ControlType.Button') { $toggle = $e; break }
    }
}
if ($toggle) {
    Write-Output "弹幕开关：IsEnabled=$($toggle.Current.IsEnabled)  Name=$($toggle.Current.Name)"
} else {
    Write-Output '没找到弹幕开关（可能被控制层淡出隐藏）'
}

# ───────────────────────── 切到弹幕分页 ─────────────────────────
Invoke-El $tab
Start-Sleep -Seconds 4
Get-WindowShot $hwnd "$Tag-2-danmaku-tab"

# 右侧列表里的文本条数（能看出是否真的在长）
$lines = 0
foreach ($e in Get-AllElements) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.X -lt ($wr.Right - 420)) { continue }
    if ([string]::IsNullOrWhiteSpace($e.Current.Name)) { continue }
    if ($e.Current.Name -match '^共 \d+ 条$') { Write-Output "列表计数：$($e.Current.Name)"; continue }
    $lines++
}
Write-Output "右栏文本元素数=$lines"

Get-WindowShot $hwnd "$Tag-3-danmaku-tab-later"

# ───────────────────────── 运行日志 ─────────────────────────
$log = Join-Path $env:LOCALAPPDATA 'AStation.Client\runtime.log'
if (Test-Path $log) {
    Write-Output '--- runtime.log（弹幕相关，本次启动后）---'
    Get-Content $log -Tail 300 | Where-Object { $_ -match '弹幕|danmaku|zt\.live|10026|10012' } | Select-Object -Last 30
}

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Output 'done'
