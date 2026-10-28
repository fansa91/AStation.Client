param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts",
    [string]$TargetTitle = "烧心",
    [switch]$NoHover,
    [switch]$Sample,
    [switch]$HideTest
)

# 验证：控制条（进度条 + 全部组件）是否与视频画面同一条、压在底部，且能 seek / 全屏
# 全程在一次调用内完成（工具调用结束后宿主会回收子进程）

Add-Type -AssemblyName System.Drawing
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

function Focus-Window($hwnd) {
    for ($i = 0; $i -lt 10; $i++) {
        [Ux]::ShowWindow($hwnd, 9) | Out-Null
        [Ux]::SetWindowPos($hwnd, [Ux]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
        [Ux]::SwitchToThisWindow($hwnd, $true) | Out-Null
        [Ux]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 300
        if ([Ux]::GetForegroundWindow() -eq $hwnd) { break }
    }
    Start-Sleep -Milliseconds 300
}

function Save-Shot([string]$name) {
    $proc = Get-AppProc
    if (-not $proc) { throw "app gone before shot $name" }
    $hwnd = $proc.MainWindowHandle
    Focus-Window $hwnd

    $r = New-Object Ux+RECT
    [Ux]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "bad window rect" }

    # 鼠标移到画面内 → 触发 PointerMoved，让自动隐藏的控制条现形
    if (-not $NoHover) {
        [Ux]::SetCursorPos([int]($r.Left + $w * 0.35), [int]($r.Top + $h * 0.22)) | Out-Null
        Start-Sleep -Milliseconds 150
        for ($i = 0; $i -lt 4; $i++) {
            [Ux]::mouse_event(0x0001, 12, 8, 0, 0)   # MOUSEEVENTF_MOVE，相对移动更可靠
            Start-Sleep -Milliseconds 50
        }
        Start-Sleep -Milliseconds 250
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()

    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    [Ux]::SetWindowPos($hwnd, [Ux]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    Write-Output "saved: $name ($w x $h)"
}

function Get-AllElements {
    $proc = Get-AppProc
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)

    # UIA 树偶尔只返回一部分（窗口刚重绘时），少于 30 个元素就重试
    $all = $null
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if (-not $win) { Start-Sleep -Milliseconds 300; continue }
        $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        if ($all -and $all.Count -gt 30) { return $all }
        Start-Sleep -Milliseconds 350
    }

    if (-not $all) { throw "window not found" }
    return $all
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
    # 退一步：点榜单里第一个视频标题
    $title = $null
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
        $r = $e.Current.BoundingRectangle
        if ($r.Width -gt 200 -and $r.Y -gt 300) { $title = $e; break }
    }
}
if (-not $title) { throw "榜单里没找到目标视频" }

$tr = $title.Current.BoundingRectangle
Write-Output "点击视频：$($title.Current.Name) @ $($tr.X),$($tr.Y)"
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)
Start-Sleep -Seconds 9

# ───────────────────────── 1. 窗口模式控制条 ─────────────────────────
Save-Shot 'cb-1-player.png'

# 鼠标在播放器内 → 控制条常显；移出播放器 3 秒后应收起
if ($HideTest) {
    $pr = Get-AppProc
    $wr = New-Object Ux+RECT
    [Ux]::GetWindowRect($pr.MainWindowHandle, [ref]$wr) | Out-Null
    Focus-Window $pr.MainWindowHandle
    # 移到右侧「分P/评论」侧栏（在 PlayerHost 之外）
    [Ux]::SetCursorPos([int]($wr.Right - 150), [int]($wr.Top + 300)) | Out-Null
    Start-Sleep -Milliseconds 300
    for ($i = 0; $i -lt 3; $i++) { [Ux]::mouse_event(0x0001, 8, 6, 0, 0); Start-Sleep -Milliseconds 60 }
    Write-Output '鼠标移出播放器，等待 4 秒…'
    Start-Sleep -Seconds 4
    $NoHover = $true
    Save-Shot 'cb-5-hidden.png'
    $hint = Find-TextLike '??:?? / ??:??*'
    Write-Output ("收起后时间文本: " + $(if ($hint) { $hint.Current.Name } else { '不在 UIA 树（控制条已收起）' }))
    $NoHover = $false

    # 恢复：鼠标回画面，让控制条重新出现（后续步骤要用）
    [Ux]::SetCursorPos([int]($wr.Left + ($wr.Right - $wr.Left) * 0.35), [int]($wr.Top + 200)) | Out-Null
    Start-Sleep -Milliseconds 300
    for ($i = 0; $i -lt 4; $i++) { [Ux]::mouse_event(0x0001, 10, 7, 0, 0); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 600
}

# 时间文本 + 进度条：确认控制条里确实有进度条与时间
$timeInit = Find-TextLike '??:?? / ??:??*'
Write-Output ("时间文本: " + $(if ($timeInit) { $timeInit.Current.Name } else { '未找到' }))

$slider = $null
foreach ($e in Get-AllElements) {
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Slider') { $slider = $e; break }
}
if (-not $slider) {
    Write-Output 'WARN: 没找到进度条 Slider'
} else {
    $sr = $slider.Current.BoundingRectangle
    Write-Output ("进度条: x={0:N0} y={1:N0} w={2:N0} h={3:N0}" -f $sr.X, $sr.Y, $sr.Width, $sr.Height)

    $rv = $null
    if ($slider.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$rv)) {
        $max = $rv.Current.Maximum
        Write-Output ("进度条范围: 0 ~ {0:N0} 秒，当前 {1:N0}" -f $max, $rv.Current.Value)

        # 拖到 55% 验证 seek
        if ($max -gt 20) {
            $rv.SetValue([double]($max * 0.55))
            Start-Sleep -Seconds 2
            Save-Shot 'cb-2-seek.png'
            $timeAfter = Find-TextLike '??:?? / ??:??*'
            if ($timeAfter) { Write-Output ("seek 后时间: " + $timeAfter.Current.Name) }
        }

        # 采样：seek 之后时间是否正常递增、会不会跑到总时长之外
        if ($Sample) {
            Write-Output '--- 采样（1 秒一次，共 10 次） ---'
            for ($i = 0; $i -lt 10; $i++) {
                [Ux]::mouse_event(0x0001, 6, 4, 0, 0)
                Start-Sleep -Milliseconds 120
                $t = Find-TextLike '??:?? / ??:??*'
                $slNow = $null
                foreach ($e in Get-AllElements) {
                    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Slider') { $slNow = $e; break }
                }
                $val = '-'
                $rvn = $null
                if ($slNow -and $slNow.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$rvn)) {
                    $val = ("{0:N1} / {1:N1}" -f $rvn.Current.Value, $rvn.Current.Maximum)
                }
                Write-Output ("[{0:00}] text={1} slider={2}" -f $i, $(if ($t) { $t.Current.Name } else { '不在UIA树(已隐藏)' }), $val)
                Start-Sleep -Seconds 1
            }
        }
    } else {
        Write-Output 'WARN: Slider 不支持 RangeValuePattern'
    }
}

# ───────────────────────── 2. 全屏 ─────────────────────────
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
    Write-Output ("全屏按钮 @ {0:N0},{1:N0}" -f $rr.X, $rr.Y)
    Click-Point ($rr.X + $rr.Width / 2) ($rr.Y + $rr.Height / 2)
    Start-Sleep -Seconds 3

    # 诊断：全屏后控制条是否还在 UIA 树里（Collapsed 时拿不到）
    [Ux]::SetCursorPos(600, 300) | Out-Null
    Start-Sleep -Milliseconds 200
    # 用相对移动制造真实的 mousemove（SetCursorPos 在视频独占渲染时不一定触发 PointerMoved）
    for ($i = 0; $i -lt 8; $i++) {
        [Ux]::mouse_event(0x0001, 30, 18, 0, 0)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 300
    $dm = Find-El '弹幕' 'Button'
    if ($dm) {
        $dr2 = $dm.Current.BoundingRectangle
        Write-Output ("全屏控制条：弹幕按钮 @ {0:N0},{1:N0} size {2:N0}x{3:N0}" -f $dr2.X, $dr2.Y, $dr2.Width, $dr2.Height)
        $sl2 = $null
        foreach ($e in Get-AllElements) {
            if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Slider') { $sl2 = $e; break }
        }
        if ($sl2) {
            $sr2 = $sl2.Current.BoundingRectangle
            Write-Output ("全屏进度条 @ {0:N0},{1:N0} w={2:N0}" -f $sr2.X, $sr2.Y, $sr2.Width)
        }
    } else {
        Write-Output '全屏后 UIA 里找不到弹幕按钮（控制条可能处于隐藏态）'
    }

    Save-Shot 'cb-3-fullscreen.png'

    # 再等一会儿确认自动隐藏生效（播放中鼠标不动 2.5 秒应收起）
    Start-Sleep -Seconds 4
    Save-Shot 'cb-4-autohide.png'
} else {
    Write-Output 'WARN: 没定位到全屏按钮'
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Output 'done'
