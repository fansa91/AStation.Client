param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts",
    [string]$TargetTitle = "烧心",
    [int]$ScrollSteps = 0
)

# 启动 → 排行榜/周榜 → 点开有楼中楼的视频 → 截图控制条 → 切评论 → 展开楼中楼 → 全屏
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
    Start-Sleep -Milliseconds 400
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
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { throw "window not found" }
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
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

# 按控件名（含类型过滤）找元素
function Find-El([string]$name, [string]$controlType) {
    foreach ($e in Get-AllElements) {
        if ($controlType -and $e.Current.ControlType.ProgrammaticName -ne "ControlType.$controlType") { continue }
        if ($e.Current.Name -eq $name) { return $e }
    }
    return $null
}

# 按文本模糊匹配
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

# ───────────────────────── 排行榜 → 周榜 ─────────────────────────
$nav = Find-El '排行榜' 'ListItem'
if (-not $nav) { $nav = Find-El '排行榜' $null }
if (-not $nav) { throw '找不到排行榜导航项' }
Invoke-El $nav | Out-Null
Start-Sleep -Seconds 3

$week = Find-El '周榜' 'Button'
if (-not $week) { $week = Find-El '周榜' $null }
if ($week) { Invoke-El $week | Out-Null; Write-Output '已切到周榜'; Start-Sleep -Seconds 6 }
else { Write-Output 'WARN: 没找到周榜按钮' }

# ───────────────────────── 点开目标视频 ─────────────────────────
$title = Wait-TextLike "*$TargetTitle*" 20
if (-not $title) { throw "排行榜里没找到包含 $TargetTitle 的视频" }

$tr = $title.Current.BoundingRectangle
Write-Output "点击视频：$($title.Current.Name) @ $($tr.X),$($tr.Y)"
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)
Start-Sleep -Seconds 8

Save-Shot 'v2-player.png'

# ───────────────────────── 评论 → 展开楼中楼 ─────────────────────────
$commentTab = Find-El '评论' 'Button'
if (-not $commentTab) { throw '没进入播放页（找不到评论分页）' }
Invoke-El $commentTab | Out-Null
Start-Sleep -Seconds 4
Save-Shot 'v3-comments.png'

# 可选：向下滚动评论列表，去找有「另有 N 条未下发」的主楼
if ($ScrollSteps -gt 0) {
    $anchor = Find-El '弹幕' 'Button'
    if ($anchor) {
        $ar = $anchor.Current.BoundingRectangle
        [Ux]::SetCursorPos([int]($ar.X + 200), [int]($ar.Y - 120)) | Out-Null
        for ($i = 0; $i -lt $ScrollSteps; $i++) {
            [Ux]::mouse_event(0x0800, 0, 0, [uint32]4294967176, 0)   # MOUSEEVENTF_WHEEL，-120 滚轮向下
            Start-Sleep -Milliseconds 220
        }
        Start-Sleep -Seconds 2
        Save-Shot 'v4-scrolled.png'
    }
}

$expand = Wait-TextLike '展开*条回复' 12
if (-not $expand) {
    Write-Output 'WARN: 没有找到 展开N条回复'
} else {
    $er = $expand.Current.BoundingRectangle
    Write-Output "展开：$($expand.Current.Name) @ $($er.X),$($er.Y)"
    Click-Point ($er.X + $er.Width / 2) ($er.Y + $er.Height / 2)
    Start-Sleep -Seconds 2
    Save-Shot 'v6-replies.png'
}

# ───────────────────────── 全屏（控制条应贴屏幕底部） ─────────────────────────
$danmaku = Find-El '弹幕' 'Button'
if ($danmaku) {
    $dr = $danmaku.Current.BoundingRectangle
    $barY = $dr.Y + $dr.Height / 2

    # 全屏按钮是控制条里最右边那个 Button（内容只有图标，没有可读名称）
    $rightMost = $null
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Button') { continue }
        $r = $e.Current.BoundingRectangle
        if ($r.Width -le 0) { continue }
        if ([Math]::Abs(($r.Y + $r.Height / 2) - $barY) -gt 14) { continue }
        if (-not $rightMost -or $r.X -gt $rightMost.Current.BoundingRectangle.X) { $rightMost = $e }
    }

    if ($rightMost) {
        $rr = $rightMost.Current.BoundingRectangle
        Write-Output "全屏按钮 @ $($rr.X),$($rr.Y) size $($rr.Width)x$($rr.Height)"
        Click-Point ($rr.X + $rr.Width / 2) ($rr.Y + $rr.Height / 2)
        Start-Sleep -Seconds 3
        Save-Shot 'v7-fullscreen.png'
    } else {
        Write-Output 'WARN: 没定位到全屏按钮'
    }
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Output 'done'
