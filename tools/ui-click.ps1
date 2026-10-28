param([int]$Dx = 240, [int]$Dy = 400)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Click {
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

# 必须声明 DPI 感知：否则 GetWindowRect 返回的是缩放后坐标，而 SetCursorPos 用物理像素，点击会错位
try { [Win32Click]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Win32Click]::SetProcessDPIAware() | Out-Null } catch { }

$proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "AStation.Client is not running" }
$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "no main window handle" }

# 不把窗口提到最前，点击会落到盖在上面的别的窗口上
for ($i = 0; $i -lt 8; $i++) {
    [Win32Click]::ShowWindow($hwnd, 9) | Out-Null
    [Win32Click]::SetWindowPos($hwnd, [Win32Click]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    [Win32Click]::SwitchToThisWindow($hwnd, $true) | Out-Null
    [Win32Click]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 250
    if ([Win32Click]::GetForegroundWindow() -eq $hwnd) { break }
}
Start-Sleep -Milliseconds 400

$r = New-Object Win32Click+RECT
[Win32Click]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$x = $r.Left + $Dx; $y = $r.Top + $Dy

[Win32Click]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 180
[Win32Click]::mouse_event([Win32Click]::LEFTDOWN, 0, 0, 0, 0)
Start-Sleep -Milliseconds 90
[Win32Click]::mouse_event([Win32Click]::LEFTUP, 0, 0, 0, 0)
Write-Output "clicked window+($Dx,$Dy)"
