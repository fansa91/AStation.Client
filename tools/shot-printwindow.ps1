# 用 PrintWindow 直接抓窗口位图（绕开 CopyFromScreen 的边缘伪影），验证右边缘是不是真有溢出内容
param(
    [string]$ExeDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64',
    [int]$W = 700,
    [int]$H = 800,
    [string]$Name = 'printwindow-700.png'
)

$ErrorActionPreference = 'Stop'
$OutDir = 'C:\Users\Administrator\Documents\pro\AStation.Client\artifacts\audit'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class PW {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@

function Get-App {
    $p = $null
    for ($i = 0; $i -lt 40; $i++) {
        $p = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { return $p }
        Start-Sleep -Milliseconds 500
    }
    throw 'app not running'
}

$p = Get-App
$r = New-Object RECT
[PW]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
[PW]::MoveWindow($p.MainWindowHandle, 300, 100, $W, $H, $true) | Out-Null
Start-Sleep -Milliseconds 2500

$r2 = New-Object RECT
[PW]::GetWindowRect($p.MainWindowHandle, [ref]$r2) | Out-Null
$w = $r2.Right - $r2.Left; $h = $r2.Bottom - $r2.Top

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT = 0x2，能抓到 WinUI3 的合成内容
[PW]::PrintWindow($p.MainWindowHandle, $hdc, 0x2) | Out-Null
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save((Join-Path $OutDir $Name), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Name (${w}x${h})"
