# P4 验收：覆盖层（搜索 / 用户空间 / 登录）+ 设置、关于页在宽窄两档的布局
param(
    [string]$ExeDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64',
    [string]$Keyword = '少女前线'
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
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int extra);
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
    Start-Sleep -Milliseconds 1200
}

function Shot([string]$name) {
    $p = Get-App
    [W]::SetWindowPos($p.MainWindowHandle, $HwndTopmost, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE)) | Out-Null
    [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600
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

function Get-All {
    $p = Get-App
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Find-El([string]$name, [string]$type = $null) {
    $want = if ($type -and -not $type.StartsWith('ControlType.')) { "ControlType.$type" } else { $type }
    foreach ($e in (Get-All)) {
        if ($e.Current.Name -ne $name) { continue }
        if ($want -and $e.Current.ControlType.ProgrammaticName -ne $want) { continue }
        return $e
    }
    return $null
}

function Find-Containing([string]$text, [string]$type = $null) {
    $want = if ($type -and -not $type.StartsWith('ControlType.')) { "ControlType.$type" } else { $type }
    foreach ($e in (Get-All)) {
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
    Start-Sleep -Milliseconds 400
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

# 取窗口内最靠上的输入框（标题栏搜索框），避免误抓到覆盖层里的输入
function Get-TopEdit {
    $best = $null; $bestY = 1e9
    foreach ($e in (Get-All)) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Edit') { continue }
        $b = $e.Current.BoundingRectangle
        if ([double]::IsNaN($b.X) -or $b.Width -le 0) { continue }
        if ($b.Y -lt $bestY) { $bestY = $b.Y; $best = $e }
    }
    return $best
}

function Send-Key([byte]$vk) {
    [W]::keybd_event($vk, 0, 0x0001, 0)
    Start-Sleep -Milliseconds 50
    [W]::keybd_event($vk, 0, 0x0003, 0)
    Start-Sleep -Milliseconds 200
}

function Esc { Send-Key 0x1B }

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$exe = Join-Path $ExeDir 'AStation.Client.exe'
Start-Process -FilePath $exe -WorkingDirectory $ExeDir
Get-App | Out-Null
Start-Sleep -Seconds 8

Resize 1440 900

# ── 设置页：宽屏 / 窄屏 ──
Log '=== 设置页 ==='
if (Select-Nav '设置') {
    Shot 'settings-1440.png'
    Resize 700 800
    Shot 'settings-700.png'
    Resize 1440 900
}

# ── 关于页 ──
Log '=== 关于页 ==='
if (Select-Nav '关于') { Shot 'about-1440.png' }

# ── 登录页（覆盖层，未登录时账号项直接进登录） ──
Log '=== 登录页 ==='
$acc = Wait-Layouted '登录' 'ListItem'
if ($acc) {
    Click-El $acc
    Start-Sleep -Milliseconds 2600
    Log ("  覆盖层打开: " + $(if (Find-El '扫码登录') { '是' } else { '否' }))
    Shot 'login-1440.png'

    Resize 860 820
    Log ("  窄屏两栏是否都在: " + $(if ((Find-El '扫码登录') -and (Find-El '账号密码登录')) { '都在' } else { '缺一块' }))
    Shot 'login-860.png'
    Resize 1440 900
    Start-Sleep -Milliseconds 800
    Esc
    Start-Sleep -Milliseconds 900
} else {
    Log '  WARN: 找不到底部「登录」导航项'
}

# ── 搜索（标题栏搜索框提交 → 独立搜索页，跳转而非覆盖层） ──
Log '=== 搜索（跳转页） ==='
$edit = Get-TopEdit
if ($edit) {
    Click-El $edit
    Start-Sleep -Milliseconds 400
    $vp = $null
    if ($edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
        $vp.SetValue($Keyword)
        Start-Sleep -Milliseconds 500
        Send-Key 0x0D   # Enter
        Start-Sleep -Milliseconds 5000
        Shot 'search-1440.png'

        # 跳转页证据：标题栏后退变可用（覆盖层时代是不变的）
        $back = Find-El '后退' 'Button'
        Log ("  后退按钮可用（说明是压进框架的页面）: " + $(if ($back -and $back.Current.IsEnabled) { '是' } else { '否' }))
        Log ("  页内搜索框: " + $(if (Find-El '搜索视频，回车查看结果' 'Edit') { '在' } else { '不在' }))
        Log ("  结果统计: " + $(if (Find-Containing '显示前') { '有' } else { '无' }))
        Log ("  排序分页：综合=" + $(if (Find-El '综合排序' 'Button') { '在' } else { '缺' }) +
             " 最多播放=" + $(if (Find-El '最多播放' 'Button') { '在' } else { '缺' }) +
             " 最新发布=" + $(if (Find-El '最新发布' 'Button') { '在' } else { '缺' }))
        Log ("  空态/失败态: " + $(if (Find-Containing '没有找到相关视频') { '空态' } else { '有结果' }))

        # 切到「最多播放」：请求要重发，结果区不能残留旧的「综合」列表
        $views = Find-El '最多播放' 'Button'
        if ($views) {
            Click-El $views
            Start-Sleep -Milliseconds 4500
            Shot 'search-sort-views.png'
            Log ("  切到最多播放后统计: " + $(if (Find-Containing '显示前') { '有' } else { '无' }))
        }

        # 清空关键词 → 页内初始态（最近搜索）
        $boxes = @(Get-All | Where-Object {
            $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and
            -not [double]::IsInfinity($_.Current.BoundingRectangle.X) -and
            $_.Current.BoundingRectangle.Y -gt 80
        })
        if ($boxes.Count -gt 0) {
            Click-El $boxes[0]
            Start-Sleep -Milliseconds 400
            $bvp = $null
            if ($boxes[0].TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$bvp)) {
                $bvp.SetValue('')
                Start-Sleep -Milliseconds 1400
                Shot 'search-initial.png'
                Log ("  最近搜索区: " + $(if (Find-El '最近搜索' 'Text') { '有' } else { '无' }))
            }
        }

        # 后退离开搜索页，验证它确实在框架栈里
        $back = Find-El '后退' 'Button'
        if ($back) {
            Click-El $back
            Start-Sleep -Milliseconds 1500
            Log ("  后退后离开搜索页: " + $(if (Find-El '搜索' 'Text') { '否（还在搜索页）' } else { '是' }))
            Shot 'search-back.png'
        }
    } else {
        Log '  WARN: 搜索框不支持 ValuePattern'
    }
} else {
    Log '  WARN: 找不到标题栏搜索框'
}

$p = Get-App
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Log 'done'
