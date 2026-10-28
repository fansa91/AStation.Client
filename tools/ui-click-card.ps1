param([string]$Match = '独立游戏', [int]$OffsetY = -70)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32CardClick {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
"@

# 必须声明 DPI 感知：否则 GetWindowRect 返回的是缩放后坐标，而 SetCursorPos 用物理像素，点击会错位
try { [Win32CardClick]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Win32CardClick]::SetProcessDPIAware() | Out-Null } catch { }

$proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "AStation.Client is not running" }
$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "no main window handle" }

for ($i = 0; $i -lt 8; $i++) {
    [Win32CardClick]::ShowWindow($hwnd, 9) | Out-Null
    [Win32CardClick]::SetWindowPos($hwnd, [Win32CardClick]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    [Win32CardClick]::SwitchToThisWindow($hwnd, $true) | Out-Null
    [Win32CardClick]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 250
    if ([Win32CardClick]::GetForegroundWindow() -eq $hwnd) { break }
}
Start-Sleep -Milliseconds 400

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$target = $null
foreach ($e in $all) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    if ($e.Current.Name -like "*$Match*") { $target = $e; break }
}

if (-not $target) { throw "找不到文本包含 '$Match' 的元素" }

$r = $target.Current.BoundingRectangle
$x = [int]($r.X + $r.Width / 2)
$y = [int]($r.Y + $OffsetY)

if ($r.Width -le 0) { throw "目标元素没有有效尺寸" }

[Win32CardClick]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 180
[Win32CardClick]::mouse_event([Win32CardClick]::LEFTDOWN, 0, 0, 0, 0)
Start-Sleep -Milliseconds 80
[Win32CardClick]::mouse_event([Win32CardClick]::LEFTUP, 0, 0, 0, 0)

Write-Output "clicked '$($target.Current.Name)' at screen ($x,$y)"
