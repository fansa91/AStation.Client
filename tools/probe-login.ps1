# 探针：登录覆盖层 —— 填账号密码、点登录触发验证码，截两张图
# 只验证 UI（验证码图 / 区分大小写提示 / 错误提示），不校验真实账号
param(
    [string]$ExeDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
)

$ErrorActionPreference = 'Stop'
$OutDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\login'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class W {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
}
"@

function Log([string]$m) { Write-Output $m }

$HwndTopmost = New-Object IntPtr (-1)
$HwndNotTopmost = New-Object IntPtr (-2)
$SWP_NOMOVE = 0x0002
$SWP_NOSIZE = 0x0001
$SWP_NOACTIVATE = 0x0010

function Get-App {
    $p = $null
    for ($i = 0; $i -lt 40; $i++) {
        $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { return $p }
        Start-Sleep -Milliseconds 500
    }
    throw 'app not running'
}

function Raise {
    $p = Get-App
    # 截图会被别的窗口盖住：先顶到最前，再撤掉置顶
    [W]::SetWindowPos($p.MainWindowHandle, $HwndTopmost, 0, 0, 0, 0, $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE) | Out-Null
    [W]::SetWindowPos($p.MainWindowHandle, $HwndNotTopmost, 0, 0, 0, 0, $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_NOACTIVATE) | Out-Null
    [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 700
}

function Shot([string]$name) {
    Raise
    $p = Get-App
    $r = New-Object RECT
    [W]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Log "  截图 $name (${w}x${h})"
}

function Get-All {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $p = Get-App
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Find-El([string]$name, [string]$type = $null) {
    foreach ($e in (Get-All)) {
        if ($e.Current.Name -ne $name) { continue }
        if ($type -and $e.Current.ControlType.ProgrammaticName -ne "ControlType.$type") { continue }
        return $e
    }
    return $null
}

function Click-El($el) {
    $r = $el.Current.BoundingRectangle
    [W]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    [W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Paste([string]$text) {
    [System.Windows.Forms.Clipboard]::SetText($text)
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('^v')
    Start-Sleep -Milliseconds 400
}

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$exe = Join-Path $ExeDir 'AStation.Client.exe'
Start-Process -FilePath $exe -WorkingDirectory $ExeDir | Out-Null

$p = Get-App
$r = New-Object RECT
[W]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
[W]::MoveWindow($p.MainWindowHandle, $r.Left, $r.Top, 1400, 900, $true) | Out-Null
[W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 6

Log '── 打开登录覆盖层 ──'
$acc = Find-El '登录' 'ListItem'
if (-not $acc) { throw '找不到底部「登录」导航项' }
Click-El $acc
Start-Sleep -Seconds 3
Shot '01-overlay.png'
Log ("  扫码登录: " + $(if (Find-El '扫码登录') { '有' } else { '无' }))

Log '── 填账号密码，点登录取验证码 ──'
# 输入框的 UIA Name 取的是 Header（账号 / 密码），顶部搜索框也叫 Edit，按名字找更稳
$user = Find-El '账号' 'Edit'
$pass = Find-El '密码' 'Edit'
if (-not $user -or -not $pass) { throw '找不到账号 / 密码输入框' }

Click-El $user
Paste '13800000000'
Click-El $pass
Paste 'wrongpass123'

$btn = Find-El '登录' 'Button'
if (-not $btn) { throw '找不到登录按钮' }
Click-El $btn
Start-Sleep -Seconds 3
Shot '02-captcha.png'

Log ("  验证码提示: " + $(if (Find-El '验证码区分大小写，看错的字符点「换一张」') { '有' } else { '无' }))
Log ("  验证码输入框: " + $(if (Find-El '请输入图中字符') { '有' } else { '无' }))
Log ("  换一张按钮: " + $(if (Find-El '换一张') { '有' } else { '无' }))

Log '── 乱填验证码再登录 ──'
$cap = Find-El '请输入图中字符' 'Edit'
if ($cap) {
    Click-El $cap
    Paste 'zzzz'
    Click-El (Find-El '登录' 'Button')
    Start-Sleep -Seconds 3
    Shot '03-error.png'
}

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Log 'done'
