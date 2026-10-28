param(
    [string]$ExeDir = "C:\Users\Administrator\Documents\pro\AStation.Client\bin\Debug\net10.0-windows10.0.26100.0\win-x64",
    [string]$OutDir = "C:\Users\Administrator\Documents\pro\AStation.Client\artifacts"
)

# 只做一件事：启动应用，抓一张整屏 + 一条任务栏，用来确认 exe 图标在任务栏里的实际观感。
# （标题栏是 ExtendsContentIntoTitleBar 自绘的，看不到图标，所以只能看任务栏）

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$exe = Join-Path $ExeDir "AStation.Client.exe"
Get-Process AStation.Client -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$proc = Start-Process -FilePath $exe -WorkingDirectory $ExeDir -PassThru
Start-Sleep -Seconds 9

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($screen.X, $screen.Y, 0, 0, $bmp.Size)
$g.Dispose()

$full = Join-Path $OutDir "taskbar-full.png"
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)

# 任务栏在最下方，取 56px；再放大 3 倍便于肉眼看 24/32px 的实际渲染
$stripH = 56
$strip = New-Object System.Drawing.Rectangle 0, ($screen.Height - $stripH), 360, $stripH
$crop = $bmp.Clone($strip, $bmp.PixelFormat)
$crop.Save((Join-Path $OutDir "taskbar-strip.png"), [System.Drawing.Imaging.ImageFormat]::Png)

$zoom = New-Object System.Drawing.Bitmap ($crop.Width * 3), ($crop.Height * 3)
$zg = [System.Drawing.Graphics]::FromImage($zoom)
$zg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$zg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$zg.DrawImage($crop, 0, 0, $zoom.Width, $zoom.Height)
$zg.Dispose()
$zoom.Save((Join-Path $OutDir "taskbar-zoom.png"), [System.Drawing.Imaging.ImageFormat]::Png)

$bmp.Dispose(); $crop.Dispose(); $zoom.Dispose()

Write-Output ("screen: {0}x{1}" -f $screen.Width, $screen.Height)
Write-Output ("app pid: {0}, has window: {1}" -f $proc.Id, ($proc.MainWindowHandle -ne 0))
Write-Output "saved taskbar-full.png / taskbar-strip.png / taskbar-zoom.png"

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
