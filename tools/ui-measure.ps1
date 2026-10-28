param([int]$TargetPid = 0)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = if ($TargetPid -gt 0) { Get-Process -Id $TargetPid -ErrorAction SilentlyContinue }
        else { Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { throw "AStation.Client is not running" }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { throw "window not found" }

function Invoke-Nav([string]$label) {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ($e.Current.Name -eq $label) {
            $p = $null
            if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
        }
    }
    return $false
}

Invoke-Nav '番剧' | Out-Null
Start-Sleep -Milliseconds 2500

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$rows = @()
foreach ($e in $all) {
    $ct = $e.Current.ControlType.ProgrammaticName
    if ($ct -eq 'ControlType.ListItem') {
        $r = $e.Current.BoundingRectangle
        if ($r.Width -gt 40 -and $r.Width -lt 500) {
            $rows += [pscustomobject]@{
                Name  = $e.Current.Name
                Left  = [int]$r.Left
                Width = [int]$r.Width
                Height= [int]$r.Height
                Top   = [int]$r.Top
            }
        }
    }
}

"total list items: $($rows.Count)"
$rows | Select-Object -First 12 | Format-Table -AutoSize
if ($rows.Count -gt 1) {
    "distinct widths: " + (($rows | Select-Object -ExpandProperty Width -Unique | Sort-Object) -join ', ')
    "distinct heights: " + (($rows | Select-Object -ExpandProperty Height -Unique | Sort-Object) -join ', ')
}
