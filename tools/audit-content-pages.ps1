# P3 内容页验收：首页 Hero / 分区条 / 推荐网格，频道、排行榜、番剧、直播大厅
param(
    [string]$ExeDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
)

$ErrorActionPreference = 'Stop'
$OutDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\audit'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class W {
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
    $p = $null
    for ($i = 0; $i -lt 40; $i++) {
        $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { return $p }
        Start-Sleep -Milliseconds 500
    }
    throw 'app not running'
}

function Get-Rect([IntPtr]$h) {
    $r = New-Object RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    return $r
}

function Resize([int]$w, [int]$h) {
    $p = Get-App
    $r = Get-Rect $p.MainWindowHandle
    [W]::MoveWindow($p.MainWindowHandle, $r.Left, $r.Top, $w, $h, $true) | Out-Null
    Start-Sleep -Milliseconds 900
}

function Shot([string]$name) {
    $p = Get-App
    [W]::SetWindowPos($p.MainWindowHandle, $HwndTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 500
    $r = Get-Rect $p.MainWindowHandle
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    [W]::SetWindowPos($p.MainWindowHandle, $HwndNotTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    Log "  shot: $name (${w}x${h})"
}

function Find-El([string]$name, [string]$type = $null) {
    $p = Get-App
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $want = if ($type -and -not $type.StartsWith('ControlType.')) { "ControlType.$type" } else { $type }
    foreach ($e in $all) {
        if ($e.Current.Name -ne $name) { continue }
        if ($want -and $e.Current.ControlType.ProgrammaticName -ne $want) { continue }
        return $e
    }
    return $null
}

function Find-Containing([string]$text, [string]$type = $null) {
    $p = Get-App
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $want = if ($type -and -not $type.StartsWith('ControlType.')) { "ControlType.$type" } else { $type }
    foreach ($e in $all) {
        $n = $e.Current.Name
        if (-not $n -or -not $n.Contains($text)) { continue }
        if ($want -and $e.Current.ControlType.ProgrammaticName -ne $want) { continue }
        return $e
    }
    return $null
}

function Click-At([int]$x, [int]$y) {
    [W]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 200
    [W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}

function Click-El($el) {
    $r = $el.Current.BoundingRectangle
    if ([double]::IsNaN($r.X) -or $r.Width -le 0) { Log '  (skip click: 元素未布局)'; return }
    Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
}

function Wait-Layouted([string]$name, [string]$type, [int]$seconds = 15) {
    for ($i = 0; $i -lt $seconds; $i++) {
        $el = Find-El $name $type
        if ($el) {
            $b = $el.Current.BoundingRectangle
            if (-not [double]::IsNaN($b.X) -and $b.Width -gt 0) { return $el }
        }
        Start-Sleep -Milliseconds 700
    }
    return $null
}

function Select-Nav([string]$name) {
    $item = Wait-Layouted $name 'ListItem'
    if (-not $item) { Log "  WARN: 找不到导航项 $name"; return $false }
    Click-El $item
    Start-Sleep -Milliseconds 2200
    return $true
}

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$exe = Join-Path $ExeDir 'AStation.Client.exe'
Start-Process -FilePath $exe -WorkingDirectory $ExeDir
Get-App | Out-Null
Start-Sleep -Seconds 8

# ── 首页 1440 ──
Log '=== 首页 1440x900 ==='
Resize 1440 900
Log ("  Hero 榜一标题: " + $(if (Find-Containing '榜一') { '在' } else { '不在' }))
Log ("  分区条「全部分区」: " + $(if (Find-El '全部分区') { '在' } else { '不在' }))
Log ("  推荐视频区块: " + $(if (Find-Containing '推荐视频') { '在' } else { '不在' }))
Log ("  换一批按钮: " + $(if (Find-Containing '换一批') { '在' } else { '不在' }))
Shot 'home-1440.png'

# ── 首页 1000（窄屏：Hero 侧栏收起） ──
Log '=== 首页 1000x720 ==='
Resize 1000 720
Shot 'home-1000.png'

Resize 1440 900
Start-Sleep -Milliseconds 600

# ── 分区胶囊点进排行榜（趁首页是全新状态，UIA 树最稳） ──
Log '--- 分区胶囊 → 排行榜 ---'
$chip = Wait-Layouted '游戏' 'Button'
if ($chip) {
    Click-El $chip
    Start-Sleep -Milliseconds 3200
    Log ("  榜单标题: " + $(if (Find-Containing '· 日榜') { '在' } else { '不在' }))
    Shot 'channel-to-rank.png'
} else {
    Log '  WARN: 找不到分区胶囊'
}

# ── 回首页后测换一批 + 卡片点击 ──
Log '--- 回首页 ---'
$homeItem = Wait-Layouted '首页' 'ListItem'
if ($homeItem) { Click-El $homeItem; Start-Sleep -Milliseconds 3000 }

Log '--- 换一批 ---'
$refresh = Wait-Layouted '换一批' 'Button'
if ($refresh) {
    Click-El $refresh
    Start-Sleep -Milliseconds 4000
    Shot 'home-refresh.png'
    Log '  已换一批（见 home-refresh.png）'
} else {
    Log '  WARN: 找不到换一批按钮'
}

Log '--- 点推荐卡片 ---'
$anchor = Find-Containing '推荐视频' 'Text'
if ($anchor) {
    $r = $anchor.Current.BoundingRectangle
    Click-At ([int]($r.X + 140)) ([int]($r.Y + 110))
    Start-Sleep -Milliseconds 5000
    Log ("  进入播放页: " + $(if (Find-El '分P') { '是' } else { '否' }))
    Shot 'home-to-player.png'

    # 从播放页返回首页
    $back = Find-El '返回' 'Button'
    if ($back) { Click-El $back; Start-Sleep -Milliseconds 2500 }
} else {
    Log '  WARN: 找不到推荐视频标题'
}

# ── 排行榜 ──
Log '=== 排行榜 1440x900 ==='
if (Select-Nav '排行榜') { Shot 'rank-1440.png' }

# ── 频道页 ──
Log '=== 频道分区 1440x900 ==='
if (Select-Nav '频道') { Shot 'channel-1440.png' }

# ── 番剧页 ──
Log '=== 番剧 1440x900 ==='
if (Select-Nav '番剧') { Shot 'bangumi-1440.png' }

# ── 直播大厅 ──
Log '=== 直播 1440x900 ==='
if (Select-Nav '直播') { Shot 'livehall-1440.png' }

$p = Get-App
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Log 'done'
