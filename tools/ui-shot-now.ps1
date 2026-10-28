param([string]$Out = 'shot.png')

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Shot {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
}
"@

# 必须声明 DPI 感知：否则 GetWindowRect 返回的是缩放后坐标，而 SetCursorPos 用物理像素，点击会错位
try { [Win32Shot]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Win32Shot]::SetProcessDPIAware() | Out-Null } catch { }

$proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "AStation.Client is not running" }
$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "no main window handle" }

# SetForegroundWindow 会被前台窗口锁挡掉，多试几次
for ($i = 0; $i -lt 8; $i++) {
    [Win32Shot]::ShowWindow($hwnd, 9) | Out-Null
    [Win32Shot]::SetWindowPos($hwnd, [Win32Shot]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    [Win32Shot]::SwitchToThisWindow($hwnd, $true) | Out-Null
    [Win32Shot]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 350
    if ([Win32Shot]::GetForegroundWindow() -eq $hwnd) { break }
}
Start-Sleep -Milliseconds 700

$r = New-Object Win32Shot+RECT
[Win32Shot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { throw "bad window rect" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()

$path = Join-Path "C:/Users/Administrator/Documents/pro/AStation.Client/artifacts" $Out
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

[Win32Shot]::SetWindowPos($hwnd, [Win32Shot]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null

$fg = [Win32Shot]::GetForegroundWindow()
$focused = if ($fg -eq $hwnd) { 'yes' } else { "no($fg)" }
Write-Output "saved: $Out ($w x $h) focused=$focused"
