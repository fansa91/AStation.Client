# 最小启动验证：应用能起到主窗口、渲染出首页内容、进程不退出
param(
    [string]$ExeDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64',
    [string]$ShotName = 'launch.png',
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Stop'
$OutDir = Join-Path $ExeDir '..\..\..\..\artifacts\audit'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$exe = Join-Path $ExeDir 'AStation.Client.exe'
if (-not (Test-Path $exe)) { throw "exe not found: $exe" }
Start-Process -FilePath $exe -WorkingDirectory $ExeDir

$proc = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc -and $proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if (-not $proc -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { throw 'app did not start' }
Write-Output "app started pid=$($proc.Id) hwnd=$($proc.MainWindowHandle)"

Start-Sleep -Seconds $WaitSeconds

# 窗口矩形
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class W32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[W32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600

$r = New-Object RECT
[W32]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Output "window: ${w}x${h} @ ($($r.Left),$($r.Top))"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$path = Join-Path $OutDir $ShotName
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "shot: $path"

# 关键元素是否渲染出来
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$texts = @()
foreach ($e in $all) {
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $e.Current.Name) { $texts += $e.Current.Name }
}
Write-Output ("texts({0}): {1}" -f $texts.Count, (($texts | Select-Object -First 12) -join ' | '))

$alive = [bool](Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)
Write-Output "alive: $alive"
if (-not $alive) {
    Write-Output '--- runtime.log tail ---'
    Get-Content "$env:LOCALAPPDATA\AStation.Client\runtime.log" -Tail 20 -ErrorAction SilentlyContinue
    throw 'app exited'
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Output 'done'
