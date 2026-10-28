Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process AStation.Client -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "AStation.Client is not running" }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

# 已知的页面固定文案，其余文本视为弹幕
$known = @(
    'AStation.Client', '搜索视频、用户、番剧', '已用时间', '剩余时间',
    '《银松镇》是今年我最看好的独立游戏', 'SteamParty', '视频简介', '分P', '银松镇'
)

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

foreach ($e in $all) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $n = $e.Current.Name
    if ([string]::IsNullOrWhiteSpace($n)) { continue }
    if ($known -contains $n) { continue }
    if ($n -match '播放|弹幕|粉丝|评论') { continue }
    if ($n -match '^\d+$') { continue }
    if ($n -match '^\d\d:\d\d') { continue }

    $r = $e.Current.BoundingRectangle
    Write-Output ("BOUNDS [" + $n + "] x=" + [int]$r.X + " y=" + [int]$r.Y + " w=" + [int]$r.Width + " h=" + [int]$r.Height)
}
