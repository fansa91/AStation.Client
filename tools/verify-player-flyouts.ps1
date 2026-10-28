param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts"
)

# 播放器 Flyout 回归：清晰度 / 线路列表改成代码创建的 ListView 后，
# 能不能打开、能不能列出档位、点了之后有没有真的换源重新起播。
#   ① 点播：清晰度切档、线路列表
#   ② 直播：码率档位切档

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Ux2 {
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

try { [Ux2]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Ux2]::SetProcessDPIAware() | Out-Null } catch { }

function Get-AppProc {
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { $p.Refresh() }
    return $p
}

function Get-Rect($hwnd) {
    $r = New-Object Ux2+RECT
    [Ux2]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    return $r
}

function Focus-Window($hwnd) {
    for ($i = 0; $i -lt 8; $i++) {
        [Ux2]::ShowWindow($hwnd, 9) | Out-Null
        [Ux2]::SetWindowPos($hwnd, [Ux2]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
        [Ux2]::SwitchToThisWindow($hwnd, $true) | Out-Null
        [Ux2]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 250
        if ([Ux2]::GetForegroundWindow() -eq $hwnd) { break }
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

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    if (-not $KeepFocus) { [Ux2]::SetWindowPos($hwnd, [Ux2]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null }
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
    [Ux2]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 150
    [Ux2]::mouse_event([Ux2]::LEFTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 70
    [Ux2]::mouse_event([Ux2]::LEFTUP, 0, 0, 0, 0)
}

function Invoke-El($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
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

function Hover-Player {
    $r = Get-Rect (Get-AppProc).MainWindowHandle
    [Ux2]::SetCursorPos([int]($r.Left + ($r.Right - $r.Left) * 0.32), [int]($r.Top + ($r.Bottom - $r.Top) * 0.22)) | Out-Null
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt 4; $i++) { [Ux2]::mouse_event(0x0001, 10, 6, 0, 0); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 400
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

function Get-FlyoutItems {
    $items = New-Object System.Collections.ArrayList
    foreach ($e in Get-AllElements) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.ListItem') { continue }
        $r = $e.Current.BoundingRectangle
        if ($r.Width -gt 0) { [void]$items.Add($e) }
    }
    return $items.ToArray()
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

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
if (-not $proc) { throw 'app did not start' }
Start-Sleep -Seconds 6

# ───── 进点播页 ─────
$nav = Find-El '排行榜' 'ListItem'
if (-not $nav) { $nav = Find-El '排行榜' $null }
Invoke-El $nav | Out-Null
Start-Sleep -Seconds 3
$day = Find-El '日榜' 'Button'
if ($day) { Invoke-El $day | Out-Null; Start-Sleep -Seconds 5 }

$title = $null
foreach ($e in Get-AllElements) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Width -gt 200 -and $r.Y -gt 300) { $title = $e; break }
}
if (-not $title) { throw '没找到视频卡片' }
$tr = $title.Current.BoundingRectangle
Focus-Window $proc.MainWindowHandle
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)
Start-Sleep -Milliseconds 600
Click-Point ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2)
for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 800; if (Find-El '播放暂停' 'Button') { break } }
Start-Sleep -Seconds 5

Write-Output ''
Write-Output '=== ① 点播：清晰度 ==='
Hover-Player
if (Click-ByName '清晰度') {
    Save-Shot 'fly-1-quality-list.png' -KeepFocus
    $items = Get-FlyoutItems
    Write-Output ("  清晰度条目：{0}" -f $items.Count)
    foreach ($it in $items) { Write-Output ("    - {0} {1}" -f $it.Current.Name, $it.Current.BoundingRectangle) }

    if ($items.Count -ge 2) {
        # 按档位名精确点击（全窗口的 ListItem 里还混着侧栏分P和导航项）
        $target = Find-El '720P' $null
        if (-not $target) { $target = Find-El '1080P' $null }
        if (-not $target) { $target = $items[0] }
        Write-Output ("  点击档位：{0}" -f $target.Current.Name)
        $r2 = $target.Current.BoundingRectangle
        Click-Point ($r2.X + $r2.Width / 2) ($r2.Y + $r2.Height / 2)
        Start-Sleep -Seconds 7
        # 先关掉浮层，否则「720P」文字还能从浮层里读到，判断会失真
        $ws0 = New-Object -ComObject WScript.Shell
        $ws0.SendKeys('{ESC}')
        Start-Sleep -Milliseconds 700
        Write-Output ('  切档后「播放暂停」：' + $(if (Find-El '播放暂停' 'Button') { '在' } else { '丢失' }))
        Write-Output ('  清晰度按钮文字已变 720P：' + $(if (Find-El '720P' 'Text') { '是' } else { '否' }))
        Hover-Player
        Save-Shot 'fly-2-quality-switched.png'
    }
}

Write-Output '=== ① 点播：线路 ==='
Hover-Player
$cdn = Find-El '线路' 'Button'
if ($cdn) {
    $cr = $cdn.Current.BoundingRectangle
    Click-Point ($cr.X + $cr.Width / 2) ($cr.Y + $cr.Height / 2)
    Start-Sleep -Milliseconds 800
    Save-Shot 'fly-3-line-list.png' -KeepFocus
    $lineItems = Get-FlyoutItems
    Write-Output ("  线路条目：{0}" -f $lineItems.Count)
    foreach ($it in $lineItems) { Write-Output ("    - {0}" -f $it.Current.Name) }
    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('{ESC}')
    Start-Sleep -Milliseconds 500
} else {
    Write-Output '  当前档位只有一条线路，按钮按设计隐藏'
}

# ───── 进直播间 ─────
Write-Output ''
Write-Output '--- 切到直播 ---'
Click-ByName '返回' | Out-Null
Start-Sleep -Seconds 2
$liveNav = Find-El '直播' 'ListItem'
if (-not $liveNav) { $liveNav = Find-El '直播' $null }
Invoke-El $liveNav | Out-Null
Start-Sleep -Seconds 6

$card = $null
foreach ($e in Get-AllElements) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.Y -gt 300 -and $r.Width -gt 80 -and $e.Current.Name.Length -ge 4) { $card = $e; break }
}
if (-not $card) { throw '直播列表没有卡片' }
$cr2 = $card.Current.BoundingRectangle
Click-Point ($cr2.X + $cr2.Width / 2) ($cr2.Y + $cr2.Height / 2)
Start-Sleep -Milliseconds 700
Click-Point ($cr2.X + $cr2.Width / 2) ($cr2.Y + $cr2.Height / 2)
for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 800; if (Find-El '清晰度' 'Button') { break } }
Start-Sleep -Seconds 8

Write-Output ''
Write-Output '=== ② 直播：码率档位 ==='
Hover-Player
if (Click-ByName '清晰度') {
    Save-Shot 'fly-4-live-quality.png' -KeepFocus
    $lq = Get-FlyoutItems
    Write-Output ("  直播档位：{0}" -f $lq.Count)
    foreach ($it in $lq) { Write-Output ("    - {0}" -f $it.Current.Name) }

    if ($lq.Count -ge 1) {
        $pick = Find-El '高清' $null
        if (-not $pick) { $pick = $lq[0] }
        Write-Output ("  切档：{0}" -f $pick.Current.Name)
        $r3 = $pick.Current.BoundingRectangle
        Click-Point ($r3.X + $r3.Width / 2) ($r3.Y + $r3.Height / 2)
        Start-Sleep -Seconds 9
        $ws3 = New-Object -ComObject WScript.Shell
        $ws3.SendKeys('{ESC}')
        Start-Sleep -Milliseconds 700
        Write-Output ('  切档后「播放暂停」：' + $(if (Find-El '播放暂停' 'Button') { '在' } else { '丢失' }))
        Write-Output ('  清晰度按钮文字已变「高清」：' + $(if (Find-El '高清' 'Text') { '是' } else { '否' }))
        Hover-Player
        Save-Shot 'fly-5-live-quality-switched.png'
    }
}

Stop-Process -Id (Get-AppProc).Id -Force -ErrorAction SilentlyContinue
Write-Output ''
Write-Output 'done'
