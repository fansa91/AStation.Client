# P1 主窗口验收：导航三态 / 标题栏密度 / 覆盖层壳
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
    # 临时置顶，保证截图时不被别的窗口（资源管理器等）压住
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
    # UIA 的 ProgrammaticName 形如 ControlType.ListItem，这里两种写法都接受
    $want = if ($type -and -not $type.StartsWith('ControlType.')) { "ControlType.$type" } else { $type }
    foreach ($e in $all) {
        if ($e.Current.Name -ne $name) { continue }
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
    Start-Sleep -Milliseconds 300
}

function Click-El($el) {
    $r = $el.Current.BoundingRectangle
    Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
}

function Nav-State {
    $item = Find-El '首页' 'ListItem'
    if (-not $item) { return 'hidden(minimal)' }
    $w = $item.Current.BoundingRectangle.Width
    if ($w -gt 120) { return "expanded($([int]$w))" }
    return "compact($([int]$w))"
}

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$exe = Join-Path $ExeDir 'AStation.Client.exe'
Start-Process -FilePath $exe -WorkingDirectory $ExeDir
Get-App | Out-Null
Start-Sleep -Seconds 7

# ── 1440：展开导航 + 完整标题栏 ──
Log '=== 1440x900 ==='
Resize 1440 900
Log "  导航: $(Nav-State)"
Log ("  搜索框: " + $(if (Find-El '搜索视频、用户、番剧' 'Edit') { '在' } else { '不在' }))
Log ("  应用名 AStation: " + $(if (Find-El 'AStation' 'Text') { '在' } else { '不在（隐藏）' }))
Shot 'win-1440.png'

# ── 1100：缩到中档（导航应转紧凑） ──
Log '=== 1100x820 ==='
Resize 1100 820
Log "  导航: $(Nav-State)"
Log ("  应用名 AStation: " + $(if (Find-El 'AStation' 'Text') { '在' } else { '不在（隐藏）' }))
Shot 'win-1100.png'

# ── 880：窄窗口（导航 minimal + 搜索收图标） ──
Log '=== 880x660 ==='
Resize 880 660
Log "  导航: $(Nav-State)"
Log ("  搜索输入框: " + $(if (Find-El '搜索视频、用户、番剧' 'Edit') { '在（未收起）' } else { '已收起' }))
Log ("  搜索图标按钮: " + $(if (Find-El '搜索' 'Button') { '在' } else { '不在' }))
Shot 'win-880.png'

# ── 窄窗口下点汉堡：覆盖式展开 ──
Log '--- 点汉堡展开导航 ---'
$hamburger = Find-El '导航' 'Button'
if ($hamburger) {
    Click-El $hamburger
    Start-Sleep -Milliseconds 900
    Log "  导航: $(Nav-State)"
    Shot 'win-880-pane.png'
    $hamburger2 = Find-El '导航' 'Button'
    if ($hamburger2) { Click-El $hamburger2; Start-Sleep -Milliseconds 700 }
} else {
    Log '  WARN: 找不到汉堡按钮'
}

# ── 覆盖层：点搜索图标 ──
Log '--- 覆盖层（搜索） ---'
$searchBtn = Find-El '搜索' 'Button'
if ($searchBtn) {
    Click-El $searchBtn
    Start-Sleep -Milliseconds 1500
    Shot 'win-overlay-search.png'
    $esc = Find-El '搜索' 'Text'
    Log ("  覆盖层内「搜索」标题: " + $(if ($esc) { '在' } else { '不在' }))
} else {
    Log '  WARN: 找不到搜索按钮'
}

# ── Esc 关闭覆盖层 ──
Add-Type -AssemblyName System.Windows.Forms
[W]::SetForegroundWindow((Get-App).MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 900
Shot 'win-overlay-closed.png'

# ── 回到 1440 检查内容限宽 ──
Log '=== 回到 1440，检查导航恢复 ==='
Resize 1440 900
Log "  导航: $(Nav-State)"

$p = Get-App
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Log 'done'
