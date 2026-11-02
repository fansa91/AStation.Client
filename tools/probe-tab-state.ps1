param(
    [string]$ExeDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64',
    [int]$WinW = 1400,
    [int]$WinH = 900,
    [string]$Tag = ''
)

$ErrorActionPreference = 'Stop'
$OutDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\tabs2'
if ($Tag) { $OutDir = Join-Path $OutDir $Tag }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECTQ { public int Left, Top, Right, Bottom; }
public static class W4 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECTQ r);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
}
"@

$Topmost = New-Object IntPtr (-1)
$NotTopmost = New-Object IntPtr (-2)
$SWP_NOMOVE = 0x0002; $SWP_NOSIZE = 0x0001; $SWP_NOACTIVATE = 0x0010

function Log([string]$m) { Write-Output $m }
function Get-App {
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw 'app not running' }
    return $p
}
function Get-Rect([IntPtr]$h) { $r = New-Object RECTQ; [W4]::GetWindowRect($h, [ref]$r) | Out-Null; return $r }
function Get-All {
    $p = Get-App
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    if ($all.Count -lt 30) {
        Start-Sleep -Milliseconds 800
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    }
    return $all
}
function Find-El([string]$name) {
    foreach ($e in (Get-All)) {
        if ($e.Current.Name -eq $name) { return $e }
    }
    return $null
}
function Click-At([int]$x, [int]$y) {
    [W4]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 180
    [W4]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [W4]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 400
}
function Ensure-Visible($el) {
    # 堆叠模式下分页行可能在视口外，先滚进视口再点
    for ($i = 0; $i -lt 8; $i++) {
        $b = $el.Current.BoundingRectangle
        $r = Get-Rect (Get-App).MainWindowHandle
        if (-not [double]::IsNaN($b.X) -and $b.Height -gt 0 -and $b.Y -gt ($r.Top + 40) -and ($b.Y + $b.Height) -lt ($r.Bottom - 20)) { return $true }
        [W4]::SetCursorPos([int]($r.Left + ($r.Right - $r.Left) / 2), [int]($r.Top + ($r.Bottom - $r.Top) * 0.6)) | Out-Null
        Start-Sleep -Milliseconds 150
        [W4]::mouse_event(0x0800, 0, 0, [uint32]4294966996, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 450
    }
    return $false
}

function Click-El($el) {
    Ensure-Visible $el | Out-Null
    $r = $el.Current.BoundingRectangle
    Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
}
function Park-Cursor {
    $p = Get-App
    $r = Get-Rect $p.MainWindowHandle
    [W4]::SetCursorPos(($r.Left + 4), ($r.Top + 200)) | Out-Null
    Start-Sleep -Milliseconds 500
}
function Shot([string]$name, [int]$cropX, [int]$cropY, [int]$cropW, [int]$cropH) {
    $p = Get-App
    [W4]::SetWindowPos($p.MainWindowHandle, $Topmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    [W4]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 700
    $r = Get-Rect $p.MainWindowHandle
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $full = Join-Path $OutDir $name
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    # 裁出右栏顶部（相对窗口坐标）
    $crop = New-Object System.Drawing.Bitmap $cropW, $cropH
    $g2 = [System.Drawing.Graphics]::FromImage($crop)
    $src = [System.Drawing.Image]::FromFile($full)
    $g2.DrawImage($src, (New-Object System.Drawing.Rectangle 0,0,$cropW,$cropH), (New-Object System.Drawing.Rectangle $cropX,$cropY,$cropW,$cropH), [System.Drawing.GraphicsUnit]::Pixel)
    $g2.Dispose()
    $cropPath = Join-Path $OutDir ('crop-' + $name)
    $crop.Save($cropPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose(); $src.Dispose()
    [W4]::SetWindowPos($p.MainWindowHandle, $NotTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    Log "  shot: $name"
}
function Tab-States {
    $out = @()
    foreach ($t in @('选集', '评论', '相关')) {
        $el = Find-El $t
        if (-not $el) { $out += "$t=?"; continue }
        $pat = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) {
            $out += ("{0}={1}" -f $t, $pat.Current.ToggleState)
        } else {
            $out += "$t=(no toggle)"
        }
    }
    return ($out -join ' ')
}

# ── 启动 ──
Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
Start-Process -FilePath (Join-Path $ExeDir 'AStation.Client.exe') -WorkingDirectory $ExeDir
Start-Sleep -Seconds 6
$p = Get-App
$r = Get-Rect $p.MainWindowHandle
[W4]::MoveWindow($p.MainWindowHandle, $r.Left, $r.Top, $WinW, $WinH, $true) | Out-Null
Start-Sleep -Seconds 2

# 首页 → 视频：Hero 主图 / 推荐卡两处都试，直到右栏分页出现
foreach ($point in @(@(0.34, 0.25), @(0.26, 0.62), @(0.26, 0.72))) {
    if (Find-El '评论') { break }
    $r = Get-Rect (Get-App).MainWindowHandle
    Click-At ([int]($r.Left + ($r.Right - $r.Left) * $point[0])) ([int]($r.Top + ($r.Bottom - $r.Top) * $point[1]))
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        if (Find-El '评论') { break }
    }
}
Start-Sleep -Seconds 3

$r = Get-Rect (Get-App).MainWindowHandle
$cw = $r.Right - $r.Left
$sideX = $cw - 330
Park-Cursor
Log ("初始: " + (Tab-States))
Shot 'a-initial.png' $sideX 40 330 170

foreach ($pair in @(@('评论', 'b-comment.png'), @('相关', 'c-related.png'), @('选集', 'd-playlist.png'), @('评论', 'e-comment2.png'))) {
    $el = Find-El $pair[0]
    if (-not $el) { Log "找不到分页 $($pair[0])"; continue }
    Click-El $el
    Start-Sleep -Milliseconds 1200
    Park-Cursor
    Log ("点 $($pair[0]) 后: " + (Tab-States))
    Shot $pair[1] $sideX 40 330 170
}

# ── 直播间：切 清晰度 / 弹幕 ──
Log '── 直播间分页 ──'
$liveNav = Find-El '直播'
if ($liveNav) { Click-El $liveNav; Start-Sleep -Seconds 5 }
$r = Get-Rect (Get-App).MainWindowHandle
Click-At ([int]($r.Left + ($r.Right - $r.Left) * 0.26)) ([int]($r.Top + ($r.Bottom - $r.Top) * 0.33))
Start-Sleep -Seconds 8
$r = Get-Rect (Get-App).MainWindowHandle
$sideX = ($r.Right - $r.Left) - 330
Park-Cursor
Shot 'f-live.png' $sideX 40 330 170
foreach ($pair in @(@('弹幕', 'g-live-danmaku.png'), @('清晰度', 'h-live-line.png'))) {
    $el = Find-El $pair[0]
    if (-not $el) { Log "找不到直播分页 $($pair[0])"; continue }
    Click-El $el
    Start-Sleep -Milliseconds 1200
    Park-Cursor
    Log "点 $($pair[0]) 后：已点击"
    Shot $pair[1] $sideX 40 330 170
}

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Log 'done'
