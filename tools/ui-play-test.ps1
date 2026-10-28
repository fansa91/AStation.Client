param([int]$TargetPid = 0)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Play {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
"@

$proc = if ($TargetPid -gt 0) { Get-Process -Id $TargetPid -ErrorAction SilentlyContinue }
        else { Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { throw "AStation.Client is not running" }
$proc.Refresh()
$hwnd = $proc.MainWindowHandle

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

function Get-Rect {
    $r = New-Object Win32Play+RECT
    [Win32Play]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    return $r
}

function Shot([string]$name) {
    [Win32Play]::ShowWindow($hwnd, 9) | Out-Null
    [Win32Play]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 900
    $rect = Get-Rect
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $path = Join-Path "C:/Users/Administrator/Documents/pro/AStation.Client/artifacts" $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved: $name ($w x $h)"
}

function Click-Relative([int]$dx, [int]$dy) {
    [Win32Play]::ShowWindow($hwnd, 9) | Out-Null
    [Win32Play]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    $rect = Get-Rect
    $x = $rect.Left + $dx; $y = $rect.Top + $dy
    [Win32Play]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [Win32Play]::mouse_event([Win32Play]::LEFTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [Win32Play]::mouse_event([Win32Play]::LEFTUP, 0, 0, 0, 0)
    Write-Output "clicked window+($dx,$dy)"
}

function Click-Name([string]$label) {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ($e.Current.Name -ne $label) { continue }
        $p = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
        if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
    }
    return $false
}

# ── 1. 首页 → 点第一个视频卡片 → 播放页 ──
Click-Name '首页' | Out-Null
Start-Sleep -Milliseconds 2500
Click-Relative 330 380
Start-Sleep -Seconds 10
Shot '09-video.png'

# ── 2. 直播 → 点第一个直播卡片 → 直播间 ──
Click-Name '直播' | Out-Null
Start-Sleep -Milliseconds 3000
Click-Relative 240 240
Start-Sleep -Seconds 12
Shot '10-live-room.png'

# ── 3. 设置页 ──
Click-Name '设置' | Out-Null
Start-Sleep -Milliseconds 2000
Shot '11-settings.png'
