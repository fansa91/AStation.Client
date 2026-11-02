param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\userpage"
)

# 我的空间（跳转页）验收：账号按钮 → 页面打开 → 两个分页 → 后退回首页
# 未登录和已登录都能跑，未登录时会看到「去登录」引导态。

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Uy {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
}
"@
try { [Uy]::SetProcessDpiAwareness(2) | Out-Null } catch { }
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process -FilePath (Join-Path $ExeDir 'AStation.Client.exe') -WorkingDirectory $ExeDir
$p = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if (-not $p) { throw 'app did not start' }
Start-Sleep -Seconds 6

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

function All { $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }

function Find-El([string]$name) {
    foreach ($e in All) { if ($e.Current.Name -eq $name) { return $e } }
    return $null
}

function Wait-El([string]$name, [int]$seconds = 10) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $e = Find-El $name
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Invoke-El($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle(); return }
    $r = $el.Current.BoundingRectangle
    [Uy]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [Uy]::mouse_event(2,0,0,0,0); Start-Sleep -Milliseconds 70; [Uy]::mouse_event(4,0,0,0,0)
}

function Save-Screen([string]$name) {
    $hwnd = (Get-Process AStation.Client | Select-Object -First 1).MainWindowHandle
    [Uy]::SetWindowPos($hwnd, [Uy]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    [Uy]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($b.Width, $b.Height)))
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [Uy]::SetWindowPos($hwnd, [Uy]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    Write-Output "shot: $name"
}

# ① 账号按钮（标题栏右上，Name=账号）
$account = Find-El '账号'
if (-not $account) { throw '没找到账号按钮' }
Invoke-El $account
Start-Sleep -Seconds 3
Save-Screen '01-open'

$historyTab = Wait-El '播放历史' 8
if (-not $historyTab) { Write-Output 'FAIL: 没有「播放历史」分页，页面可能没打开'; }
else {
    Write-Output "OK: 我的空间已打开（分页：$($historyTab.Current.ControlType.ProgrammaticName)）"

    # ② 收藏分页
    $collect = Find-El '收藏'
    if ($collect) { Invoke-El $collect; Start-Sleep -Seconds 2; Save-Screen '02-collect' }
    else { Write-Output 'WARN: 没找到收藏分页' }

    # ③ 回到播放历史
    Invoke-El $historyTab; Start-Sleep -Seconds 2; Save-Screen '03-history'
}

# ④ 后退按钮（标题栏左侧）
$back = Find-El '后退'
if ($back) {
    Write-Output "后退可用=$($back.Current.IsEnabled)"
    Invoke-El $back
    Start-Sleep -Seconds 2
    Save-Screen '04-back'
} else { Write-Output 'WARN: 没找到后退按钮' }

# ⑤ 登录页仍应是覆盖层：点账号（未登录）→ 看是否有 scrim
$account = Find-El '账号'
if ($account) { Invoke-El $account; Start-Sleep -Seconds 2; Save-Screen '05-login-overlay' }

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output 'done'
