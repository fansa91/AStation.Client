param(
    [string]$Prefix = 'burst',
    [int]$Count = 6,
    [int]$IntervalMs = 1800,
    [int]$WarmupMs = 2500
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Burst {
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
try { [Win32Burst]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Win32Burst]::SetProcessDPIAware() | Out-Null } catch { }

$proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "AStation.Client is not running" }
$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "no main window handle" }

for ($i = 0; $i -lt 8; $i++) {
    [Win32Burst]::ShowWindow($hwnd, 9) | Out-Null
    [Win32Burst]::SetWindowPos($hwnd, [Win32Burst]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    [Win32Burst]::SwitchToThisWindow($hwnd, $true) | Out-Null
    [Win32Burst]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 250
    if ([Win32Burst]::GetForegroundWindow() -eq $hwnd) { break }
}

Start-Sleep -Milliseconds $WarmupMs

$r = New-Object Win32Burst+RECT
[Win32Burst]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { throw "bad window rect" }

$dir = "C:/Users/Administrator/Documents/pro/AStation.Client/artifacts"

for ($i = 1; $i -le $Count; $i++) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()

    $name = "{0}-{1}.png" -f $Prefix, $i
    $bmp.Save((Join-Path $dir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved $name"

    if ($i -lt $Count) { Start-Sleep -Milliseconds $IntervalMs }
}

[Win32Burst]::SetWindowPos($hwnd, [Win32Burst]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
