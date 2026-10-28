param([string]$ControlType = 'ComboBox')

# 列出指定类型的 UIA 元素及其 Name / 边界，用于定位难以命名的控件
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "AStation.Client is not running" }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

foreach ($e in $all) {
    $ct = $e.Current.ControlType.ProgrammaticName
    if ($ct -ne "ControlType.$ControlType") { continue }
    $r = $e.Current.BoundingRectangle
    # 部分容器会返回 ∞ 尺寸，跳过即可
    if ([double]::IsInfinity($r.Width) -or [double]::IsInfinity($r.Height)) { continue }
    if ([double]::IsNaN($r.Width) -or $r.Width -le 0) { continue }
    Write-Output ("[{0}] name='{1}' rect=({2},{3},{4},{5})" -f $ct, $e.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
}
