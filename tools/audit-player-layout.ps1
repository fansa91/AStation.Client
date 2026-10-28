# P2 播放页布局验收：点播 / 直播 × 多宽度 + 全屏
param(
    [string]$ExeDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
)

$ErrorActionPreference = 'Stop'
$OutDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\audit'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class W2 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
}
"@

$HwndTopmost = New-Object IntPtr (-1)
$HwndNotTopmost = New-Object IntPtr (-2)
$SWP_NOMOVE = 0x0002
$SWP_NOSIZE = 0x0001
$SWP_NOACTIVATE = 0x0010

function Log([string]$m) { Write-Output $m }

function Get-App {
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p -or $p.MainWindowHandle -eq [IntPtr]::Zero) { throw 'app not running' }
    return $p
}

function Get-Rect([IntPtr]$h) {
    $r = New-Object RECT
    [W2]::GetWindowRect($h, [ref]$r) | Out-Null
    return $r
}

function Resize([int]$w, [int]$h) {
    $p = Get-App
    $r = Get-Rect $p.MainWindowHandle
    [W2]::MoveWindow($p.MainWindowHandle, $r.Left, $r.Top, $w, $h, $true) | Out-Null
    Start-Sleep -Milliseconds 1200
}

function Shot([string]$name) {
    $p = Get-App
    [W2]::SetWindowPos($p.MainWindowHandle, $HwndTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    [W2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600
    $r = Get-Rect $p.MainWindowHandle
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    [W2]::SetWindowPos($p.MainWindowHandle, $HwndNotTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    Log "  shot: $name (${w}x${h})"
}

function Get-All {
    $p = Get-App
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Find-El([string]$name, [string]$type = $null) {
    $all = Get-All
    $want = if ($type -and -not $type.StartsWith('ControlType.')) { "ControlType.$type" } else { $type }
    foreach ($e in $all) {
        if ($e.Current.Name -ne $name) { continue }
        if ($want -and $e.Current.ControlType.ProgrammaticName -ne $want) { continue }
        return $e
    }
    return $null
}

function Click-At([int]$x, [int]$y) {
    [W2]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 200
    [W2]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [W2]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Click-El($el) {
    $r = $el.Current.BoundingRectangle
    if ([double]::IsNaN($r.X) -or $r.Width -le 0) { Write-Output '  (skip click: 元素未布局)'; return }
    Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
}

function Wait-Layouted([string]$name, [string]$type, [int]$seconds = 15) {
    for ($i = 0; $i -lt $seconds; $i++) {
        $el = Find-El $name $type
        if ($el) {
            $b = $el.Current.BoundingRectangle
            if (-not [double]::IsNaN($b.X) -and $b.Width -gt 0) { return $el }
        }
        Start-Sleep -Milliseconds 600
    }
    return $null
}

function Select-Nav([string]$name) {
    $el = Wait-Layouted $name 'ListItem'
    if (-not $el) { throw "找不到导航项 $name" }
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) {
        $pat.Select()
    } else {
        Invoke-El $el
    }
    Start-Sleep -Seconds 4
}

function Invoke-El($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return }
    $s = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$s)) { $s.Select(); return }
    Click-El $el
}

function Hover-Player {
    $p = Get-App
    $r = Get-Rect $p.MainWindowHandle
    [W2]::SetCursorPos([int]($r.Left + ($r.Right - $r.Left) / 2), [int]($r.Top + 260)) | Out-Null
    Start-Sleep -Milliseconds 200
    [W2]::mouse_event(0x0001, 30, 16, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 400
}

function Wait-Vod([int]$seconds = 30) {
    # 控制层会在鼠标离开播放器后自动收起，收起后 Slider 不在 UIA 树里。
    # 判断是否进入播放页时要先把指针停在播放器区域内。
    for ($i = 0; $i -lt $seconds; $i++) {
        $p = Get-App
        $r = Get-Rect $p.MainWindowHandle
        [W2]::SetCursorPos([int]($r.Left + ($r.Right - $r.Left) / 2), [int]($r.Top + 260)) | Out-Null
        [W2]::mouse_event(0x0001, 20, 10, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300
        if (Find-El '播放进度' 'Slider') { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# ───────────────────────── 启动 ─────────────────────────
Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Start-Process -FilePath (Join-Path $ExeDir 'AStation.Client.exe') -WorkingDirectory $ExeDir
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
Log "app started pid=$($p.Id)"
Start-Sleep -Seconds 6

Resize 1600 960

# ───────────────────────── 点播 ─────────────────────────
Select-Nav '排行榜'
$day = Find-El '日榜' 'Button'
if (-not $day) { $day = Find-El '日榜' $null }
if ($day) { Invoke-El $day | Out-Null; Start-Sleep -Seconds 5 }

$title = $null
for ($try = 0; $try -lt 20 -and -not $title; $try++) {
    foreach ($e in Get-All) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
        $r = $e.Current.BoundingRectangle
        if ([double]::IsNaN($r.X)) { continue }
        if ($r.Width -gt 200 -and $r.Y -gt 300) { $title = $e; break }
    }
    if (-not $title) { Start-Sleep -Milliseconds 700 }
}
if (-not $title) { throw '榜单里没找到可点的视频' }

$tr = $title.Current.BoundingRectangle
[W2]::SetForegroundWindow((Get-App).MainWindowHandle) | Out-Null
Click-At ([int]($tr.X + 40)) ([int]($tr.Y + $tr.Height / 2))
Start-Sleep -Milliseconds 600
Click-At ([int]($tr.X + 40)) ([int]($tr.Y + $tr.Height / 2))

if (-not (Wait-Vod 32)) { throw '没有进入点播页' }
Log '=== 点播页 ==='
Start-Sleep -Seconds 4

foreach ($size in @(@(1600, 960, 'vod-1600'), @(1440, 900, 'vod-1440'), @(1200, 860, 'vod-1200'), @(1000, 720, 'vod-1000'))) {
    Resize $size[0] $size[1]
    Hover-Player
    Log ("  --- {0}x{1} ---" -f $size[0], $size[1])
    Log ("    控制条按钮：播放暂停=" + $(if (Find-El '播放暂停' 'Button') { '在' } else { '缺' }) +
         " 倍速=" + $(if (Find-El '倍速' 'Button') { '在' } else { '缺' }) +
         " 全屏=" + $(if (Find-El '全屏' 'Button') { '在' } else { '缺' }))
    Log ("    评论标题：" + $(if (Find-El '评论' 'Text') { '在' } else { '缺' }) +
         " 选集分页=" + $(if (Find-El '选集' 'Button') { '在' } else { '缺' }) +
         " 相关分页=" + $(if (Find-El '相关' 'Button') { '在' } else { '缺' }))
    Shot ($size[2] + '.png')
}

# 全屏
Log '--- 点播全屏 ---'
Resize 1440 900
Hover-Player
$fs = Find-El '全屏' 'Button'
if ($fs) {
    Click-El $fs
    Start-Sleep -Milliseconds 1200
    $rf = Get-Rect (Get-App).MainWindowHandle
    Log ("    全屏窗口 {0}x{1} @ ({2},{3})" -f ($rf.Right - $rf.Left), ($rf.Bottom - $rf.Top), $rf.Left, $rf.Top)
    [W2]::SetCursorPos([int]($rf.Left + 60), [int]($rf.Top + 56)) | Out-Null
    for ($i = 0; $i -lt 6; $i++) { [W2]::mouse_event(0x0001, 40, 24, 0, 0); Start-Sleep -Milliseconds 50 }
    Start-Sleep -Milliseconds 400
    Shot 'vod-fullscreen.png'

    [W2]::SetForegroundWindow((Get-App).MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 2
    $rx = Get-Rect (Get-App).MainWindowHandle
    Log ("    退出全屏 {0}x{1} @ ({2},{3})" -f ($rx.Right - $rx.Left), ($rx.Bottom - $rx.Top), $rx.Left, $rx.Top)
}

# ───────────────────────── 直播 ─────────────────────────
Log '=== 直播页 ==='
Resize 1440 900
$back = Find-El '返回' 'Button'
if ($back) { Click-El $back; Start-Sleep -Seconds 2 } else { Log 'WARN: 没有返回按钮' }

Select-Nav '直播'
Start-Sleep -Seconds 4

$card = $null
foreach ($e in Get-All) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Y -gt 300 -and $r.Width -gt 80 -and $e.Current.Name.Length -ge 4) { $card = $e; break }
}
if (-not $card) { throw '直播列表里没找到卡片' }

$cr = $card.Current.BoundingRectangle
Log "  点击直播间：$($card.Current.Name)"
[W2]::SetForegroundWindow((Get-App).MainWindowHandle) | Out-Null
Click-At ([int]($cr.X + 20)) ([int]($cr.Y + $cr.Height / 2))
Start-Sleep -Milliseconds 700
Click-At ([int]($cr.X + 20)) ([int]($cr.Y + $cr.Height / 2))

$entered = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 800
    if (Find-El '清晰度' 'Button') { $entered = $true; break }
}
if (-not $entered) { throw '没有进入直播间' }
Log '  已进入直播间'

foreach ($size in @(@(1600, 960, 'live-1600'), @(1440, 900, 'live-1440'), @(1200, 860, 'live-1200'), @(1000, 720, 'live-1000'))) {
    Resize $size[0] $size[1]
    Hover-Player
    Log ("  --- {0}x{1} ---" -f $size[0], $size[1])
    Log ("    控制条：播放暂停=" + $(if (Find-El '播放暂停' 'Button') { '在' } else { '缺' }) +
         " 清晰度=" + $(if (Find-El '清晰度' 'Button') { '在' } else { '缺' }) +
         " 全屏=" + $(if (Find-El '全屏' 'Button') { '在' } else { '缺' }))
    Log ("    进度条（直播不应有）=" + $(if (Find-El '播放进度' 'Slider') { '有' } else { '无' }) +
         " 弹幕分页=" + $(if (Find-El '弹幕' 'Button') { '在' } else { '缺' }))
    Shot ($size[2] + '.png')
}

# 切到弹幕分页
$dm = Find-El '弹幕' 'Button'
if ($dm) { Click-El $dm; Start-Sleep -Milliseconds 900; Shot 'live-danmaku-tab.png' }

Stop-Process -Id (Get-App).Id -Force -ErrorAction SilentlyContinue
Log 'done'
