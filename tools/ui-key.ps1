param([int]$Vk = 0x1B)

# 向前台窗口发送一次按键（默认 Esc）。用 keybd_event，避免依赖 COM。
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Key {
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
}
"@

[Win32Key]::keybd_event([byte]$Vk, 0, 0x0001, 0)   # KEYEVENTF_EXTENDEDKEY 置位便于某些控件识别
Start-Sleep -Milliseconds 60
[Win32Key]::keybd_event([byte]$Vk, 0, 0x0003, 0)   # KEYEVENTF_KEYUP | EXTENDEDKEY

Write-Output ("sent vk=0x{0:X2}" -f $Vk)
