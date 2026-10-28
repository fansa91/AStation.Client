param([int]$X, [int]$Y, [int]$DelayMs = 250)

# 按屏幕绝对坐标点击（UIA 给出的 BoundingRectangle 就是这套坐标）
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32ClickXY {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
}
"@

try { [Win32ClickXY]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Win32ClickXY]::SetProcessDPIAware() | Out-Null } catch { }

$p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { throw "AStation.Client is not running" }

[Win32ClickXY]::SetWindowPos($p.MainWindowHandle, [Win32ClickXY]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
[Win32ClickXY]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 350

[Win32ClickXY]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds $DelayMs
[Win32ClickXY]::mouse_event(2, 0, 0, 0, 0)
Start-Sleep -Milliseconds 80
[Win32ClickXY]::mouse_event(4, 0, 0, 0, 0)

Write-Output "clicked ($X,$Y)"
