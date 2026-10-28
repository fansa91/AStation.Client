param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts",
    [string]$TargetTitle = "烧心",
    [switch]$NoSpeed
)

# 验证：① 启动窗口尺寸 ② 倍速播放 ③ 全屏白边 ④ 退出全屏提示自动消失
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

function Save-Shot([string]$name, [switch]$Raw) {
    $proc = Get-AppProc
    if (-not $proc) { throw "app gone before shot $name" }
    $hwnd = $proc.MainWindowHandle
    if (-not $Raw) { Focus-Window $hwnd }

    $r = Get-Rect $hwnd
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "bad window rect" }

    if (-not $Raw) {
        [Ux]::SetCursorPos([int]($r.Left + $w * 0.35), [int]($r.Top + $h * 0.22)) | Out-Null
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

    # 进程可能有多个顶层窗口（比如 ComboBox 弹出层留下的隐藏 HWND），
    # 只取第一个会漏元素 —— 把所有顶层窗口的子树合并起来
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        $all = New-Object System.Collections.ArrayList
        foreach ($w in $wins) {
            $sub = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($s in $sub) { [void]$all.Add($s) }
        }
        if ($all.Count -gt 30) { return $all.ToArray() }
        Start-Sleep -Milliseconds 350
    }
    if (-not $all -or $all.Count -eq 0) { throw "window not found" }
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
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return $true }
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

function Find-TextLike([string]$pattern) {
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
        if ($e.Current.Name -like $pattern) { return $e }
    }
    return $null
}

function Wait-TextLike([string]$pattern, [int]$seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $e = Find-TextLike $pattern
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Hover-Player {
    # 播放中鼠标不在画面里时控制条会收起，UIA 里就找不到进度条/按钮了 —— 先hover一下
    $r = Get-Rect (Get-AppProc).MainWindowHandle
    $x = [int]($r.Left + ($r.Right - $r.Left) * 0.35)
    $y = [int]($r.Top + ($r.Bottom - $r.Top) * 0.30)
    [Ux]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt 4; $i++) { [Ux]::mouse_event(0x0001, 10, 6, 0, 0); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 300
}

function Get-Slider {
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Slider') { return $e }
    }
    return $null
}

function Get-SliderValue {
    $s = Get-Slider
    if (-not $s) { return $null }
    $rv = $null
    if ($s.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$rv)) {
        return [double]$rv.Current.Value
    }
    return $null
}

# 播放 5 秒，看进度条走了多少秒（用于判断倍速是否真的生效）
function Measure-Advance([int]$seconds = 5) {
    Hover-Player
    $v0 = Get-SliderValue
    $t0 = Get-Date
    Start-Sleep -Seconds $seconds
    $v1 = Get-SliderValue
    $wall = ((Get-Date) - $t0).TotalSeconds
    if ($null -eq $v0 -or $null -eq $v1) { return "n/a" }
    $rate = ($v1 - $v0) / $wall
    Write-Host ("  进度 {0:N1}s → {1:N1}s，墙钟 {2:N1}s，倍率≈{3:N2}x" -f $v0, $v1, $wall, $rate)
    return $rate
}

function Select-Speed([string]$label) {
    Hover-Player
    # 控制条里三个下拉框从左到右是：倍速 / 清晰度 / 线路。
    # UIA 里 ComboBox 的 Name 常常是空的（Placeholder 才给名字），所以按位置挑最左那个。
    $bar = Find-El '弹幕' 'Button'
    if (-not $bar) { $bar = Find-El '弹幕' $null }
    $barY = if ($bar) { $bar.Current.BoundingRectangle.Y + $bar.Current.BoundingRectangle.Height / 2 } else { -1 }

    $cands = @()
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.ComboBox') { continue }
        $r = $e.Current.BoundingRectangle
        if ($r.Width -le 0) { continue }
        if ($barY -gt 0 -and ([Math]::Abs(($r.Y + $r.Height / 2) - $barY) -gt 24)) { continue }
        $cands += $e
    }
    Write-Host ("控制条里的下拉框: " + (($cands | ForEach-Object { "'" + $_.Current.Name + "'@" + [int]$_.Current.BoundingRectangle.X }) -join ', '))

    $combo = $null
    foreach ($e in $cands) {
        if (-not $combo -or $e.Current.BoundingRectangle.X -lt $combo.Current.BoundingRectangle.X) { $combo = $e }
    }
    if (-not $combo) { Write-Host "WARN: 没找到倍速下拉框"; return $false }

    $ec = $null
    if (-not $combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ec)) {
        Write-Host "WARN: 下拉框不支持 ExpandCollapse"; return $false
    }
    $ec.Expand()
    Start-Sleep -Milliseconds 700

    $item = Find-El $label 'ListItem'
    if (-not $item) { $item = Find-El $label $null }
    if ($item) {
        Invoke-El $item | Out-Null
        Start-Sleep -Milliseconds 600
        Write-Host "已选择倍速 $label"
        return $true
    }

    Write-Host "WARN: 展开后找不到 $label 项"
    $ec.Collapse()
    return $false
}

# ───────────────────────── 启动 ─────────────────────────
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
if (-not $proc -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "app did not start" }
Write-Output "app started pid=$($proc.Id)"
Start-Sleep -Seconds 6

# ① 初始窗口尺寸
$r0 = Get-Rect $proc.MainWindowHandle
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
Write-Output ("初始窗口: {0}x{1} @ ({2},{3})  (工作区 {4}x{5})" -f `
    ($r0.Right - $r0.Left), ($r0.Bottom - $r0.Top), $r0.Left, $r0.Top, $screen.WorkingArea.Width, $screen.WorkingArea.Height)

# ───────────────────────── 排行榜 → 日榜 → 打开视频 ─────────────────────────
$nav = Find-El '排行榜' 'ListItem'
if (-not $nav) { $nav = Find-El '排行榜' $null }
if (-not $nav) { throw '找不到排行榜导航项' }
Invoke-El $nav | Out-Null
Start-Sleep -Seconds 3

$day = Find-El '日榜' 'Button'
if (-not $day) { $day = Find-El '日榜' $null }
if ($day) { Invoke-El $day | Out-Null; Write-Output '已切到日榜'; Start-Sleep -Seconds 5 }

$title = Wait-TextLike "*$TargetTitle*" 20
if (-not $title) {
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
        $r = $e.Current.BoundingRectangle
        if ($r.Width -gt 200 -and $r.Y -gt 300) { $title = $e; break }
    }
}
if (-not $title) { throw "榜单里没找到目标视频" }

$tr = $title.Current.BoundingRectangle
Write-Output "点击视频：$($title.Current.Name) @ $($tr.X),$($tr.Y)"
Focus-Window $proc.MainWindowHandle
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)
Start-Sleep -Milliseconds 600
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)

# 轮询进入播放页（播放页有进度条 Slider，榜单页没有）
$entered = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 800
    if (Get-Slider) { $entered = $true; break }
}
if (-not $entered) { throw '点击后没有进入播放页' }
Start-Sleep -Seconds 6

Save-Shot 'v2-1-window.png'

# ───────────────────────── ② 倍速 ─────────────────────────
if (-not $NoSpeed) {
Write-Output '--- 1.0x 基准 ---'
$rate1 = Measure-Advance 5

Write-Output '--- 切到 2.0x ---'
if (Select-Speed '2.0x') {
    Start-Sleep -Seconds 1
    $rate2 = Measure-Advance 5
    Save-Shot 'v2-2-speed.png'
}

Write-Output '--- 切回 1.0x ---'
Select-Speed '1.0x' | Out-Null
Start-Sleep -Seconds 1
}

# ───────────────────────── ③④ 全屏 ─────────────────────────
Hover-Player
$bar = Find-El '弹幕' 'Button'
if (-not $bar) { $bar = Find-El '弹幕' $null }
$barY = if ($bar) { $bar.Current.BoundingRectangle.Y + $bar.Current.BoundingRectangle.Height / 2 } else { 0 }

$rightMost = $null
foreach ($e in Get-AllElements) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Button') { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Width -le 0 -or $r.Width -gt 200) { continue }
    if ([Math]::Abs(($r.Y + $r.Height / 2) - $barY) -gt 16) { continue }
    if ($e.Current.Name -eq '弹幕') { continue }
    if (-not $rightMost -or $r.X -gt $rightMost.Current.BoundingRectangle.X) { $rightMost = $e }
}

if ($rightMost) {
    $rr = $rightMost.Current.BoundingRectangle
    Click-Point ($rr.X + $rr.Width / 2) ($rr.Y + $rr.Height / 2)
    Start-Sleep -Milliseconds 1200

    $rf = Get-Rect (Get-AppProc).MainWindowHandle
    Write-Output ("全屏窗口: {0}x{1} @ ({2},{3})" -f ($rf.Right - $rf.Left), ($rf.Bottom - $rf.Top), $rf.Left, $rf.Top)

    $hintNow = Find-El '退出全屏' $null
    Write-Output ("刚进全屏：「退出全屏」提示 " + $(if ($hintNow) { '存在' } else { '不存在' }))
    Save-Shot 'v2-3-fullscreen.png' -Raw

    # 不碰鼠标，等 5 秒：控制条 + 退出全屏提示都应自动消失
    Start-Sleep -Seconds 5
    $hintLater = Find-El '退出全屏' $null
    Write-Output ("5 秒后：「退出全屏」提示 " + $(if ($hintLater) { '仍在（未自动消失）' } else { '已消失' }))
    Save-Shot 'v2-4-fs-hidden.png' -Raw

    # 鼠标动一下 → 应重新出现
    $rc = Get-Rect (Get-AppProc).MainWindowHandle
    for ($i = 0; $i -lt 8; $i++) { [Ux]::mouse_event(0x0001, 25, 15, 0, 0); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 500
    $hintBack = Find-El '退出全屏' $null
    Write-Output ("鼠标移动后：「退出全屏」提示 " + $(if ($hintBack) { '已重新出现' } else { '没出现（应检查 PointerMoved 订阅）' }))
    # 立刻截一张（不聚焦不挪鼠标，赶在 2.5 秒自动收起之前）
    Save-Shot 'v2-6-fs-controls.png' -Raw

    # Esc 退出全屏 → 窗口尺寸还原
    [Ux]::SwitchToThisWindow((Get-AppProc).MainWindowHandle, $true) | Out-Null
    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('{ESC}')
    Start-Sleep -Seconds 2
    $rx = Get-Rect (Get-AppProc).MainWindowHandle
    Write-Output ("退出全屏后窗口: {0}x{1} @ ({2},{3})" -f ($rx.Right - $rx.Left), ($rx.Bottom - $rx.Top), $rx.Left, $rx.Top)
    Save-Shot 'v2-5-exited.png' -Raw
} else {
    Write-Output 'WARN: 没定位到全屏按钮'
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Output 'done'
