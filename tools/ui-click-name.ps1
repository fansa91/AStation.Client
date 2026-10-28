param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$ControlType = 'Button',
    [int]$OffsetY = 0
)

# 按 UIA 控件名精确点击（tab、按钮这类没有独立文本节点的元素用它）
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32ClickName {
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
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
"@

try { [Win32ClickName]::SetProcessDpiAwareness(2) | Out-Null } catch { }
try { [Win32ClickName]::SetProcessDPIAware() | Out-Null } catch { }

$proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "AStation.Client is not running" }
$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "no main window handle" }

for ($i = 0; $i -lt 8; $i++) {
    [Win32ClickName]::ShowWindow($hwnd, 9) | Out-Null
    [Win32ClickName]::SetWindowPos($hwnd, [Win32ClickName]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
    [Win32ClickName]::SwitchToThisWindow($hwnd, $true) | Out-Null
    [Win32ClickName]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 250
    if ([Win32ClickName]::GetForegroundWindow() -eq $hwnd) { break }
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
    $ct = $e.Current.ControlType.ProgrammaticName
    if ($ct -ne "ControlType.$ControlType") { continue }
    if ($e.Current.Name -eq $Name) { $target = $e; break }
}

if (-not $target) { throw "找不到 ControlType=$ControlType 且 Name='$Name' 的元素" }

$r = $target.Current.BoundingRectangle
if ($r.Width -le 0) { throw "目标元素没有有效尺寸" }

$x = [int]($r.X + $r.Width / 2)
$y = [int]($r.Y + $r.Height / 2) + $OffsetY

[Win32ClickName]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 180
[Win32ClickName]::mouse_event([Win32ClickName]::LEFTDOWN, 0, 0, 0, 0)
Start-Sleep -Milliseconds 80
[Win32ClickName]::mouse_event([Win32ClickName]::LEFTUP, 0, 0, 0, 0)

Write-Output "clicked '$($target.Current.Name)' ($ControlType) at screen ($x,$y)"
