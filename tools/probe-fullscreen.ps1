param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\fullscreen",
    [string]$Tag = "fs"
)

# 全屏专项探针：窗口态 → 全屏 → 鼠标移出/移入 → Esc 退出，每步都量窗口矩形并整屏截图。
# 整屏截图能看出「画面没铺满 / 白边 / 控制条错位 / 黑边比例不对」。
# 子进程在本脚本内启动、结束，避免宿主回收。

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Fx {
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
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
"@

try { [Fx]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Fx]::SetProcessDPIAware() | Out-Null } catch { }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-AppProc {
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { $p.Refresh() }
    return $p
}

function Get-WinRect($hwnd) {
    $r = New-Object Fx+RECT
    [Fx]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    return "$($r.Left),$($r.Top) $($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
}

function Focus-Window($hwnd) {
    for ($i = 0; $i -lt 8; $i++) {
        [Fx]::ShowWindow($hwnd, 9) | Out-Null
        [Fx]::SetWindowPos($hwnd, [Fx]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
        [Fx]::SwitchToThisWindow($hwnd, $true) | Out-Null
        [Fx]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 200
        if ([Fx]::GetForegroundWindow() -eq $hwnd) { break }
    }
}

# 整屏截图：全屏类问题（白边 / 没铺满）只有看屏幕才准
function Save-Screen([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, (New-Object System.Drawing.Size($b.Width, $b.Height)))
    $g.Dispose()
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "shot: $path ($($b.Width)x$($b.Height))"
}

function Get-AllElements {
    $proc = Get-AppProc
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { return @() }
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
}

function Click-Point([double]$x, [double]$y) {
    [Fx]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 150
    [Fx]::mouse_event([Fx]::LEFTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 70
    [Fx]::mouse_event([Fx]::LEFTUP, 0, 0, 0, 0)
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
Write-Output "app pid=$($proc.Id)  window=$(Get-WinRect $proc.MainWindowHandle)"
Start-Sleep -Seconds 6

# ───────────────────────── 进播放页 ─────────────────────────
# 首页「推荐视频」的卡片是 Group（206x204，Name 是标题），不是 Button；
# 排行榜页顶部还有大图 Hero，所以按尺寸筛：只要推荐区里那种小卡片
$cards = @()
foreach ($e in Get-AllElements) {
    $ct = $e.Current.ControlType.ProgrammaticName
    if ($ct -notin @('ControlType.Group', 'ControlType.Button')) { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Width -lt 150 -or $r.Width -gt 420) { continue }
    if ($r.Height -lt 120 -or $r.Height -gt 420) { continue }
    if ($r.Y -lt 700) { continue }
    if ([string]::IsNullOrWhiteSpace($e.Current.Name)) { continue }
    $cards += $e
}
if ($cards.Count -eq 0) { throw '没有可点的视频卡片' }
$card = $cards[0]
$cr = $card.Current.BoundingRectangle
Write-Output "点卡片：$($card.Current.Name) @ $($cr.X),$($cr.Y) $($cr.Width)x$($cr.Height)"
Click-Point ($cr.X + $cr.Width / 2) ($cr.Y + $cr.Height / 2)
Start-Sleep -Seconds 9

$mode = Wait-El '全屏' 15
if (-not $mode) { throw '没进入播放页' }
Write-Output "进入播放页，window=$(Get-WinRect (Get-AppProc).MainWindowHandle)"

$hwnd = (Get-AppProc).MainWindowHandle
Focus-Window $hwnd
# 鼠标停在画面中间，让控制层露出来
$wr = New-Object Fx+RECT
[Fx]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
[Fx]::SetCursorPos([int](($wr.Left + $wr.Right) / 2), [int](($wr.Top + $wr.Bottom) / 2)) | Out-Null
Start-Sleep -Milliseconds 900
Save-Screen "$Tag-1-windowed"

# ───────────────────────── 找全屏按钮 ─────────────────────────
function Find-FullscreenButton {
    # PlayerOverlay 里的按钮都带 AutomationProperties.Name，直接用名字找最稳
    return Find-El '全屏'
}

$fsBtn = Find-FullscreenButton
if (-not $fsBtn) { throw '没定位到全屏按钮' }
$fr = $fsBtn.Current.BoundingRectangle
Write-Output "全屏按钮 @ $($fr.X),$($fr.Y) $($fr.Width)x$($fr.Height)"
Click-Point ($fr.X + $fr.Width / 2) ($fr.Y + $fr.Height / 2)
Start-Sleep -Seconds 3

$proc = Get-AppProc
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output "全屏后 window=$(Get-WinRect $proc.MainWindowHandle)  屏幕=$($screen.Width)x$($screen.Height)"

# ① 指针还停在按钮位置（应该自动收起）
Save-Screen "$Tag-2-fullscreen-controls"
Start-Sleep -Seconds 4
Save-Screen "$Tag-3-fullscreen-idle"

# ② 鼠标移到画面中间再移到底部，看控制条是否正常出现
[Fx]::SetCursorPos([int]($screen.Width / 2), [int]($screen.Height / 2)) | Out-Null
Start-Sleep -Milliseconds 700
[Fx]::SetCursorPos([int]($screen.Width / 2), $screen.Height - 60) | Out-Null
Start-Sleep -Milliseconds 900
Save-Screen "$Tag-4-fullscreen-hover"

# ③ 鼠标甩到屏幕左上角，画面应完全无遮拦
[Fx]::SetCursorPos(2, 2) | Out-Null
Start-Sleep -Seconds 4
Save-Screen "$Tag-5-fullscreen-clean"

# ───────────────────────── Esc 退出 ─────────────────────────
[Fx]::keybd_event(0x1B, 0, 0, [IntPtr]::Zero)
[Fx]::keybd_event(0x1B, 0, 2, [IntPtr]::Zero)
Start-Sleep -Seconds 3
$proc = Get-AppProc
if ($proc) { Write-Output "Esc 后 window=$(Get-WinRect $proc.MainWindowHandle)" }
Save-Screen "$Tag-6-after-esc"

# ───────────────────────── 运行日志里的全屏记录 ─────────────────────────
$log = Join-Path $env:LOCALAPPDATA 'AStation.Client\runtime.log'
if (Test-Path $log) {
    Write-Output '--- runtime.log（fullscreen / window 相关，本次启动后）---'
    Get-Content $log -Tail 200 | Where-Object { $_ -match 'fullscreen|无边框|抵消|player=' } | Select-Object -Last 25
}

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Output 'done'
