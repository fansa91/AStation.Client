param(
    [string]$OutDir = "$PSScriptRoot\..\artifacts",
    [int]$TargetPid = 0
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Shoter {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$proc = if ($TargetPid -gt 0) { Get-Process -Id $TargetPid -ErrorAction SilentlyContinue }
        else { Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { throw "AStation.Client is not running" }

$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "No main window handle" }

[Win32Shoter]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
[Win32Shoter]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 700

function Save-WindowShot([string]$name) {
    $rect = New-Object Win32Shoter+RECT
    [Win32Shoter]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { throw "Invalid window rect" }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()

    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved: $path ($w x $h)"
}

function Invoke-NavItem([string]$label) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { return $false }

    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ($e.Current.Name -eq $label) {
            $p = $null
            if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) {
                $p.Select(); return $true
            }
            if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) {
                $p.Invoke(); return $true
            }
        }
    }
    return $false
}

$script:pages = @(
    @{ Label = '首页';   File = '01-home.png' },
    @{ Label = '频道';   File = '02-channel.png' },
    @{ Label = '排行榜'; File = '03-rank.png' },
    @{ Label = '番剧';   File = '04-bangumi.png' },
    @{ Label = '直播';   File = '05-live.png' },
    @{ Label = '登录';   File = '06-login.png' }
)

foreach ($p in $script:pages) {
    if (Invoke-NavItem $p.Label) {
        Start-Sleep -Milliseconds 2500
        Save-WindowShot $p.File
    } else {
        Write-Output "skip (not found): $($p.Label)"
    }
}

# back to home
Invoke-NavItem '首页' | Out-Null
