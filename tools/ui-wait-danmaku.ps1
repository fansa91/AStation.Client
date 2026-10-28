param([int]$TimeoutSeconds = 120, [string]$Out = 'danmaku-hit.png')

$log = Join-Path $env:LOCALAPPDATA "AStation.Client\runtime.log"
if (-not (Test-Path $log)) { throw "no runtime log" }

$shotScript = Join-Path $PSScriptRoot 'ui-shot-now.ps1'
$startCount = @(Get-Content $log).Count
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 350
    $lines = @(Get-Content $log)
    if ($lines.Count -le $startCount) { continue }

    $newLines = $lines[$startCount..($lines.Count - 1)]
    $startCount = $lines.Count

    $hit = $newLines | Where-Object { $_ -match '投放' } | Select-Object -First 1
    if ($hit) {
        & powershell -ExecutionPolicy Bypass -File $shotScript -Out $Out | Out-Null
        Write-Output "HIT: $hit"
        exit 0
    }
}

Write-Output "TIMEOUT: no danmaku in ${TimeoutSeconds}s"
Get-Content $log | Select-Object -Last 6
