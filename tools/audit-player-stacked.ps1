# 一次性验收：1000 窄屏堆叠下右栏（评论分页 + 列表限高 280）
# 注意：WinUI 的 UIA 会把「滚出 ScrollViewer 视口的元素」报成 IsOffscreen + 空矩形，
# 所以折叠在首屏下方的侧栏不能靠 UIA 矩形判断，要用 ScrollPattern 真滚下去看。
# 窗口要给足高度（1000x1000）：堆叠时播放器 16:9 占 516，720 高的窗户内容区只剩 109，
# 340 的侧栏卡片露不全，tabs 会被挤到窗口下沿之外点不到。
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
public static class W3 {
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
    [W3]::GetWindowRect($h, [ref]$r) | Out-Null
    return $r
}

function Resize([int]$w, [int]$h) {
    $p = Get-App
    $r = Get-Rect $p.MainWindowHandle
    [W3]::MoveWindow($p.MainWindowHandle, $r.Left, $r.Top, $w, $h, $true) | Out-Null
    Start-Sleep -Milliseconds 1400
}

function Shot([string]$name) {
    $p = Get-App
    [W3]::SetWindowPos($p.MainWindowHandle, $HwndTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    [W3]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600
    $r = Get-Rect $p.MainWindowHandle
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    [W3]::SetWindowPos($p.MainWindowHandle, $HwndNotTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    Log "  shot: $name (${w}x${h})"
}

function Get-All {
    $p = Get-App
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Find-El([string]$name, [string]$type = $null) {
    $want = if ($type -and -not $type.StartsWith('ControlType.')) { "ControlType.$type" } else { $type }
    foreach ($e in Get-All) {
        if ($e.Current.Name -ne $name) { continue }
        if ($want -and $e.Current.ControlType.ProgrammaticName -ne $want) { continue }
        return $e
    }
    return $null
}

function Find-Id([string]$id) {
    foreach ($e in Get-All) {
        if ($e.Current.AutomationId -eq $id) { return $e }
    }
    return $null
}

function Click-At([int]$x, [int]$y) {
    [W3]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 200
    [W3]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [W3]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Invoke-El($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return }
    $s = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$s)) { $s.Select(); return }
    Click-El $el
}

function Click-El($el) {
    $b = $el.Current.BoundingRectangle
    if ([double]::IsNaN($b.X) -or [double]::IsInfinity($b.X) -or $b.Width -le 0) { Log '  (skip click: 元素不在视口内，先滚动)'; return }
    Click-At ([int]($b.X + $b.Width / 2)) ([int]($b.Y + $b.Height / 2))
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

# ───────────────────────── 进点播页 ─────────────────────────
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
[W3]::SetForegroundWindow((Get-App).MainWindowHandle) | Out-Null
Click-At ([int]($tr.X + 40)) ([int]($tr.Y + $tr.Height / 2))
Start-Sleep -Milliseconds 600
Click-At ([int]($tr.X + 40)) ([int]($tr.Y + $tr.Height / 2))
Start-Sleep -Seconds 5

# ───────────────────────── 1000 堆叠 ─────────────────────────
# 窗口必须给足高度：堆叠时播放器 16:9 占 516，内容区剩下的要能容纳 340 的侧栏卡片，
# 720 高的窗户内容区只剩 109，卡片物理上露不全（tabs 会被挤到窗口下沿之外）。
Log '=== 1000x1000 堆叠模式 ==='
Resize 1000 1000
Start-Sleep -Milliseconds 600

# 用 ScrollPattern 把页面滚下去（侧栏折叠在首屏下方）。
# 只能滚到侧栏顶部刚进视口；再往下 tabs 会滚过头，UIA 就只剩 offscreen 空矩形、反而点不到分页。
$scroller = Find-Id 'BodyScroll'
if (-not $scroller) { throw '找不到 BodyScroll' }
$sp = $null
if (-not $scroller.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$sp)) { throw 'BodyScroll 不支持 ScrollPattern' }
Log ("    滚动前 offset={0}" -f $sp.Current.VerticalScrollPercent)
$sp.SetScrollPercent(-1, 50)
Start-Sleep -Milliseconds 1200
Log ("    滚动后 offset={0}（可滚={1} 视口占比={2}）" -f $sp.Current.VerticalScrollPercent, $sp.Current.VerticallyScrollable, $sp.Current.VerticalViewportSize)
Shot 'vod-1000-stacked.png'

# 侧栏进视口了，量一下分页与评论列表
$tabEp = Find-El '选集' 'Button'
if ($tabEp -and -not [double]::IsInfinity($tabEp.Current.BoundingRectangle.X)) {
    $b = $tabEp.Current.BoundingRectangle
    Log ("    选集分页 @({0},{1}) {2}x{3}" -f [int]$b.X, [int]$b.Y, [int]$b.Width, [int]$b.Height)
}
$cm = Find-El '评论' 'Button'
if ($cm) {
    Invoke-El $cm | Out-Null
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 500
        $items = @(Get-All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and -not [double]::IsInfinity($_.Current.BoundingRectangle.X) })
        if ($items.Count -gt 0) { break }
    }
    Log ("    评论分页可见 ListItem=$($items.Count)（含左导航 8 项 → 评论约 $($items.Count - 8) 条）")

    # 堆叠模式下评论列表限高 280，不能把整个页面拖长
    $cl = Find-Id 'CommentList'
    if ($cl) {
        $b = $cl.Current.BoundingRectangle
        if ([double]::IsNaN($b.X) -or [double]::IsInfinity($b.X)) {
            Log '    WARN: CommentList 空矩形（滚出视口）'
        } else {
            Log ("    CommentList 高={0}（堆叠限高 280，应 ≤280）" -f [int]$b.Height)
        }
    } else {
        Log '    WARN: 找不到 CommentList'
    }

    # 滚动条状态：列表自己滚，页面 extent 不应再被评论撑爆
    Log ("    评论态 offset={0}（列表内部自行滚动）" -f $sp.Current.VerticalScrollPercent)
    Shot 'vod-1000-comment.png'

    # 原地切回选集验证互斥（不滚回顶部，否则 tabs 又掉出视口）
    $ep = Find-El '选集' 'Button'
    if ($ep) {
        Invoke-El $ep | Out-Null
        Start-Sleep -Milliseconds 1200
        $left = @(Get-All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and -not [double]::IsInfinity($_.Current.BoundingRectangle.X) })
        $cmt = @($left | Where-Object { $_.Current.Name -match '^(赞|回复|展开)' })
        Log ("    切回选集后 ListItem=$($left.Count)（应只剩分P 项 + 导航 8）；评论残留=$($cmt.Count) 应为 0")
    }
} else {
    Log 'WARN: 找不到评论分页'
}
Log 'done'
