param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts"
)

# 统一播放器回归：点播页 + 直播页共用 PlayerOverlay
# ① 点播：控制层元素清单、进度推进、全屏 / 自动收起 / Esc
# ② 直播：无进度与倍速、清晰度档位、右侧线路分页、全屏
# 全部写在一次调用里（工具调用结束后宿主会回收子进程）

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Ux {
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
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
"@

try { [Ux]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Ux]::SetProcessDPIAware() | Out-Null } catch { }

function Get-AppProc {
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { $p.Refresh() }
    return $p
}

function Get-Rect($hwnd) {
    $r = New-Object Ux+RECT
    [Ux]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    return $r
}

function Focus-Window($hwnd) {
    for ($i = 0; $i -lt 8; $i++) {
        [Ux]::ShowWindow($hwnd, 9) | Out-Null
        [Ux]::SetWindowPos($hwnd, [Ux]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
        [Ux]::SwitchToThisWindow($hwnd, $true) | Out-Null
        [Ux]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 250
        if ([Ux]::GetForegroundWindow() -eq $hwnd) { break }
    }
    Start-Sleep -Milliseconds 200
}

function Save-Shot([string]$name, [switch]$Raw, [switch]$KeepFocus) {
    $proc = Get-AppProc
    if (-not $proc) { throw "app gone before shot $name" }
    $hwnd = $proc.MainWindowHandle

    if (-not $KeepFocus) { Focus-Window $hwnd }

    $r = Get-Rect $hwnd
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "bad window rect" }

    if (-not $Raw) {
        [Ux]::SetCursorPos([int]($r.Left + $w * 0.32), [int]($r.Top + $h * 0.22)) | Out-Null
        Start-Sleep -Milliseconds 150
        for ($i = 0; $i -lt 4; $i++) {
            [Ux]::mouse_event(0x0001, 12, 8, 0, 0)
            Start-Sleep -Milliseconds 50
        }
        Start-Sleep -Milliseconds 250
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    if (-not $Raw) { [Ux]::SetWindowPos($hwnd, [Ux]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null }
    Write-Output "saved: $name ($w x $h)"
}

function Get-AllElements {
    $proc = Get-AppProc
    if (-not $proc) { throw "app not running" }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)

    $all = New-Object System.Collections.ArrayList
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $all.Clear()
        $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        foreach ($w in $wins) {
            $sub = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($s in $sub) { [void]$all.Add($s) }
        }
        if ($all.Count -gt 30) { return $all.ToArray() }
        Start-Sleep -Milliseconds 350
    }
    return $all.ToArray()
}

function Click-Point([double]$x, [double]$y) {
    [Ux]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 150
    [Ux]::mouse_event([Ux]::LEFTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 70
    [Ux]::mouse_event([Ux]::LEFTUP, 0, 0, 0, 0)
}

function Invoke-El($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
    $r = $el.Current.BoundingRectangle
    if ($r.Width -gt 0 -and $r.Height -gt 0) { Click-Point ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2); return $true }
    return $false
}

function Find-El([string]$name, [string]$controlType) {
    foreach ($e in Get-AllElements) {
        if ($controlType -and $e.Current.ControlType.ProgrammaticName -ne "ControlType.$controlType") { continue }
        if ($e.Current.Name -eq $name) { return $e }
    }
    return $null
}

function Get-SliderByName([string]$name) {
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Slider') { continue }
        if ($e.Current.Name -eq $name) { return $e }
    }
    return $null
}

function Get-SliderValue([string]$name) {
    foreach ($s in Get-AllElements) {
        if ($s.Current.ControlType.ProgrammaticName -ne 'ControlType.Slider') { continue }
        $rv = $null
        if (-not $s.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$rv)) { continue }
        # 进度条的 Maximum 是视频秒数；音量是 1、倍速是 2
        if ($rv.Current.Maximum -gt 60) { return [double]$rv.Current.Value }
    }
    return $null
}

function Hover-Player {
    $r = Get-Rect (Get-AppProc).MainWindowHandle
    $x = [int]($r.Left + ($r.Right - $r.Left) * 0.32)
    $y = [int]($r.Top + ($r.Bottom - $r.Top) * 0.22)
    [Ux]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt 4; $i++) { [Ux]::mouse_event(0x0001, 10, 6, 0, 0); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 400
}

function Measure-Advance([int]$seconds = 5) {
    Hover-Player
    $v0 = Get-SliderValue '播放进度'
    $t0 = Get-Date
    Start-Sleep -Seconds $seconds
    $v1 = Get-SliderValue '播放进度'
    $wall = ((Get-Date) - $t0).TotalSeconds
    if ($null -eq $v0 -or $null -eq $v1) { return "n/a" }
    $rate = ($v1 - $v0) / $wall
    Write-Output ("  进度 {0:N1}s -> {1:N1}s，墙钟 {2:N1}s，倍率≈{3:N2}x" -f $v0, $v1, $wall, $rate)
    return $rate
}

function Click-ByName([string]$name, [string]$type = 'Button') {
    Hover-Player
    $el = Find-El $name $type
    if (-not $el) { $el = Find-El $name $null }
    if (-not $el) { Write-Output "WARN: 找不到「$name」"; return $false }
    $r = $el.Current.BoundingRectangle
    Click-Point ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
    Start-Sleep -Milliseconds 700
    return $true
}

function Dump-Controls([string[]]$names) {
    foreach ($n in $names) {
        $el = Find-El $n $null
        if ($el) { Write-Output ("  [{0}] 存在 {1}" -f $n, $el.Current.BoundingRectangle) }
        else { Write-Output ("  [{0}] 缺失" -f $n) }
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ═══════════════════════ 启动 ═══════════════════════
Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$exe = Join-Path $ExeDir 'AStation.Client.exe'
Start-Process -FilePath $exe -WorkingDirectory $ExeDir

$proc = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-AppProc
    if ($proc -and $proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if (-not $proc -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { throw 'app did not start' }
Write-Output "app started pid=$($proc.Id)"
Start-Sleep -Seconds 6

# ═══════════════════════ ① 点播：排行榜 → 打开视频 ═══════════════════════
$nav = Find-El '排行榜' 'ListItem'
if (-not $nav) { $nav = Find-El '排行榜' $null }
if (-not $nav) { throw '找不到排行榜导航项' }
Invoke-El $nav | Out-Null
Start-Sleep -Seconds 3

$day = Find-El '日榜' 'Button'
if (-not $day) { $day = Find-El '日榜' $null }
if ($day) { Invoke-El $day | Out-Null; Start-Sleep -Seconds 5 }

$title = $null
foreach ($e in Get-AllElements) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Width -gt 200 -and $r.Y -gt 300) { $title = $e; break }
}
if (-not $title) { throw '榜单里没找到可点的视频' }

$tr = $title.Current.BoundingRectangle
Focus-Window $proc.MainWindowHandle
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)
Start-Sleep -Milliseconds 600
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)

$entered = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 800
    if (Get-SliderByName '播放进度') { $entered = $true; break }
}
if (-not $entered) { throw '点击后没有进入点播页' }

Write-Output ''
Write-Output '=== ① 点播页（窗口模式）==='
Start-Sleep -Seconds 5
Hover-Player
Save-Shot 'unified-1-vod-window.png'
Dump-Controls @('返回', '上一集', '后退10秒', '播放暂停', '前进10秒', '下一集', '静音', '音量', '倍速', '弹幕开关', '清晰度', '线路', '全屏')
Write-Output ('  进度：' + [string](Get-SliderValue '播放进度'))

$rate1 = Measure-Advance 4
Write-Output ("  播放倍率≈{0:N2}x" -f $rate1)

Write-Output '--- 点播全屏 ---'
Hover-Player
if (Click-ByName '全屏') {
    Start-Sleep -Milliseconds 900
    $rf = Get-Rect (Get-AppProc).MainWindowHandle
    Write-Output ("  全屏窗口: {0}x{1} @ ({2},{3})" -f ($rf.Right - $rf.Left), ($rf.Bottom - $rf.Top), $rf.Left, $rf.Top)
    [Ux]::SetCursorPos([int]($rf.Left + 60), [int]($rf.Top + 56)) | Out-Null
    for ($i = 0; $i -lt 6; $i++) { [Ux]::mouse_event(0x0001, 40, 24, 0, 0); Start-Sleep -Milliseconds 50 }
    Start-Sleep -Milliseconds 350
    Save-Shot 'unified-2-vod-fullscreen.png' -Raw -KeepFocus
    Write-Output ('  顶栏「退出全屏」：' + $(if (Find-El '退出全屏' $null) { '存在' } else { '不存在' }))

    Start-Sleep -Seconds 5
    Save-Shot 'unified-3-vod-fullscreen-idle.png' -Raw -KeepFocus
    Write-Output ('  5 秒后「退出全屏」：' + $(if (Find-El '退出全屏' $null) { '仍在（未收起）' } else { '已收起' }))

    [Ux]::SwitchToThisWindow((Get-AppProc).MainWindowHandle, $true) | Out-Null
    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('{ESC}')
    Start-Sleep -Seconds 2
    $rx = Get-Rect (Get-AppProc).MainWindowHandle
    Write-Output ("  退出全屏后窗口: {0}x{1} @ ({2},{3})" -f ($rx.Right - $rx.Left), ($rx.Bottom - $rx.Top), $rx.Left, $rx.Top)
}

# ═══════════════════════ ② 返回 → 直播列表 → 第一个直播间 ═══════════════════════
Write-Output ''
Write-Output '--- 返回并进入直播 ---'
if (-not (Click-ByName '返回')) { Write-Output 'WARN: 顶栏没有返回按钮' }
Start-Sleep -Seconds 2

$liveNav = Find-El '直播' 'ListItem'
if (-not $liveNav) { $liveNav = Find-El '直播' $null }
if (-not $liveNav) { throw '找不到直播导航项' }
Invoke-El $liveNav | Out-Null
Start-Sleep -Seconds 6

$card = $null
foreach ($e in Get-AllElements) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Y -gt 300 -and $r.Width -gt 80 -and $e.Current.Name.Length -ge 4) { $card = $e; break }
}
if (-not $card) { throw '直播列表里没找到可点的卡片' }
$cr = $card.Current.BoundingRectangle
Write-Output "点击直播间：$($card.Current.Name)"
Click-Point ($cr.X + $cr.Width / 2) ($cr.Y + $cr.Height / 2)
Start-Sleep -Milliseconds 700
Click-Point ($cr.X + $cr.Width / 2) ($cr.Y + $cr.Height / 2)

$liveEntered = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 800
    if (Find-El '清晰度' 'Button') { $liveEntered = $true; break }
}
if (-not $liveEntered) { throw '没有进入直播间' }

Write-Output ''
Write-Output '=== ② 直播页（窗口模式）==='
Start-Sleep -Seconds 8
Hover-Player
Save-Shot 'unified-4-live-window.png'
Dump-Controls @('返回', '播放暂停', '静音', '音量', '清晰度', '弹幕开关', '全屏', '直播信息', '线路')
Write-Output ('  进度条「播放进度」：' + $(if (Get-SliderByName '播放进度') { '存在（直播不应有）' } else { '无（符合预期）' }))
Write-Output ('  倍速按钮：' + $(if (Find-El '倍速' $null) { '存在（直播不应有）' } else { '无（符合预期）' }))

Write-Output '--- 直播线路分页 ---'
$lineTab = Find-El '线路' 'Button'
if ($lineTab) {
    $lr = $lineTab.Current.BoundingRectangle
    Click-Point ($lr.X + $lr.Width / 2) ($lr.Y + $lr.Height / 2)
    Start-Sleep -Milliseconds 900
    Save-Shot 'unified-5-live-lines.png'
    $items = 0
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $items++ }
    }
    Write-Output ("  线路档位列表项：{0}" -f $items)
} else {
    Write-Output '  WARN: 找不到「线路」分页'
}

Write-Output '--- 直播全屏 ---'
Hover-Player
if (Click-ByName '全屏') {
    Start-Sleep -Milliseconds 900
    $rf2 = Get-Rect (Get-AppProc).MainWindowHandle
    Write-Output ("  全屏窗口: {0}x{1} @ ({2},{3})" -f ($rf2.Right - $rf2.Left), ($rf2.Bottom - $rf2.Top), $rf2.Left, $rf2.Top)
    [Ux]::SetCursorPos([int]($rf2.Left + 60), [int]($rf2.Top + 56)) | Out-Null
    for ($i = 0; $i -lt 6; $i++) { [Ux]::mouse_event(0x0001, 40, 24, 0, 0); Start-Sleep -Milliseconds 50 }
    Start-Sleep -Milliseconds 350
    Save-Shot 'unified-6-live-fullscreen.png' -Raw -KeepFocus

    [Ux]::SwitchToThisWindow((Get-AppProc).MainWindowHandle, $true) | Out-Null
    $ws2 = New-Object -ComObject WScript.Shell
    $ws2.SendKeys('{ESC}')
    Start-Sleep -Seconds 2
    $rx2 = Get-Rect (Get-AppProc).MainWindowHandle
    Write-Output ("  退出全屏后窗口: {0}x{1} @ ({2},{3})" -f ($rx2.Right - $rx2.Left), ($rx2.Bottom - $rx2.Top), $rx2.Left, $rx2.Top)
    Hover-Player
    Save-Shot 'unified-7-live-after-fullscreen.png'
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Output ''
Write-Output 'done'
