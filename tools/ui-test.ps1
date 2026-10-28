param(
    [int]$TargetPid = 0,
    [string]$ExePath = "",
    [int]$MaxDepth = 8,
    [switch]$Interactive
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ErrorActionPreference = 'Stop'
$AU = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-WindowForPid([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AU::ProcessIdProperty, $procId)
    $el = $AU::RootElement.FindFirst($TS::Children, $cond)
    if (-not $el) { $el = $AU::RootElement.FindFirst($TS::Descendants, $cond) }
    return $el
}

function Format-El($el, [int]$depth) {
    $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    $name = $el.Current.Name
    $aid = $el.Current.AutomationId
    if ($name.Length -gt 42) { $name = $name.Substring(0, 42) + '…' }
    $indent = '  ' * $depth
    $line = "$indent[$ct]"
    if ($name) { $line += " name='$name'" }
    if ($aid) { $line += " id='$aid'" }
    return $line
}

function Walk($el, [int]$depth, [int]$maxDepth, [System.Collections.Generic.List[string]]$out) {
    if ($depth -gt $maxDepth) { return }
    $out.Add((Format-El $el $depth))
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($el)
    while ($child) {
        Walk $child ($depth + 1) $maxDepth $out
        $child = $walker.GetNextSibling($child)
    }
}

# ---------------------------------------------------------------------------
# 1. 启动或连接应用
# ---------------------------------------------------------------------------
$proc = $null
if ($TargetPid -gt 0) {
    $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
}

if (-not $proc) {
    $exe = if ($ExePath) { $ExePath } else {
        Join-Path $PSScriptRoot "..\bin\Debug\net10.0-windows10.0.26100.0\win-x64\AStation.Client.exe"
    }
    if (-not (Test-Path $exe)) { throw "找不到可执行文件: $exe" }
    Write-Host ">>> 启动应用: $exe" -ForegroundColor Cyan
    $proc = Start-Process -FilePath $exe -PassThru
}

Write-Host ">>> 进程 PID = $($proc.Id)" -ForegroundColor Cyan

# 等待主窗口出现（最多 30 秒）
$window = $null
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) { throw "应用已退出，退出码 0x$('{0:X8}' -f $proc.ExitCode)" }
    $window = Get-WindowForPid $proc.Id
    if ($window) { break }
    Start-Sleep -Milliseconds 500
}
if (-not $window) { throw "30 秒内未找到应用主窗口" }

Write-Host ""
Write-Host "=================== 窗口信息 ===================" -ForegroundColor Green
Write-Host "标题      : $($window.Current.Name)"
Write-Host "类名      : $($window.Current.ClassName)"
Write-Host "句柄      : 0x$('{0:X}' -f $window.Current.NativeWindowHandle)"
$r = $window.Current.BoundingRectangle
Write-Host "尺寸      : $([int]$r.Width) x $([int]$r.Height)"
Write-Host "可交互    : $(-not $window.Current.IsOffscreen)"

# ---------------------------------------------------------------------------
# 2. 输出控件树
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=================== 控件树 ===================" -ForegroundColor Green
$lines = New-Object System.Collections.Generic.List[string]
Walk $window 0 $MaxDepth $lines
$lines | ForEach-Object { Write-Host $_ }

# ---------------------------------------------------------------------------
# 3. 关键元素可用性检查
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=================== 关键元素检查 ===================" -ForegroundColor Green

$checks = @(
    @{ Name = '搜索框';       Desc = '查找 AutoSuggestBox' },
    @{ Name = '导航-首页';    Desc = '查找 NavigationViewItem 首页' },
    @{ Name = '导航-频道';    Desc = '查找 NavigationViewItem 频道' },
    @{ Name = '导航-排行榜';  Desc = '查找 NavigationViewItem 排行榜' },
    @{ Name = '导航-番剧';    Desc = '查找 NavigationViewItem 番剧' },
    @{ Name = '导航-直播';    Desc = '查找 NavigationViewItem 直播' },
    @{ Name = '导航-登录';    Desc = '查找 NavigationViewItem 登录' },
    @{ Name = '导航-设置';    Desc = '查找 NavigationViewItem 设置' }
)

$all = $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Host "控件总数: $($all.Count)"

foreach ($name in @('首页','频道','排行榜','番剧','直播','登录','设置','搜索视频、用户、番剧')) {
    $hit = $null
    foreach ($e in $all) {
        if ($e.Current.Name -eq $name) { $hit = $e; break }
    }
    if ($hit) {
        $ct = $hit.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        Write-Host ("  [OK]   {0,-22} -> {1}" -f $name, $ct) -ForegroundColor Green
    } else {
        Write-Host ("  [MISS] {0}" -f $name) -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# 4. 交互测试：依次切换导航项
# ---------------------------------------------------------------------------
if ($Interactive) {
    Write-Host ""
    Write-Host "=================== 导航交互测试 ===================" -ForegroundColor Green

    foreach ($item in @('频道','排行榜','番剧','直播','首页')) {
        $target = $null
        foreach ($e in $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($e.Current.Name -eq $item) { $target = $e; break }
        }
        if (-not $target) {
            Write-Host ("  [SKIP] {0} 未找到" -f $item) -ForegroundColor Yellow
            continue
        }

        $pattern = $null
        if ($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select()
        } elseif ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            $pattern.Invoke()
        } else {
            Write-Host ("  [SKIP] {0} 不支持 Select/Invoke" -f $item) -ForegroundColor Yellow
            continue
        }

        Start-Sleep -Milliseconds 1500
        $proc.Refresh()
        if ($proc.HasExited) {
            Write-Host ("  [FAIL] 点击 {0} 后应用崩溃，退出码 0x{1:X8}" -f $item, $proc.ExitCode) -ForegroundColor Red
            break
        }
        Write-Host ("  [OK]   {0} 导航成功，进程存活" -f $item) -ForegroundColor Green
    }

    # 登录页覆盖层测试
    Write-Host ""
    Write-Host "--- 登录覆盖层测试 ---" -ForegroundColor Cyan
    $loginItem = $null
    foreach ($e in $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -eq '登录') { $loginItem = $e; break }
    }
    if ($loginItem) {
        $pattern = $null
        if ($loginItem.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select()
        }
        Start-Sleep -Seconds 2
        $proc.Refresh()
        if ($proc.HasExited) {
            Write-Host ("  [FAIL] 打开登录页后崩溃，退出码 0x{0:X8}" -f $proc.ExitCode) -ForegroundColor Red
        } else {
            Write-Host "  [OK]   登录页打开，进程存活" -ForegroundColor Green
            $names = @()
            foreach ($e in $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($e.Current.Name) { $names += $e.Current.Name }
            }
            Write-Host ("  登录页可见文本: {0}" -f (($names | Select-Object -First 15) -join ' | '))
        }
    }
}

# ---------------------------------------------------------------------------
# 5. 汇总
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=================== 结论 ===================" -ForegroundColor Green
$proc.Refresh()
if ($proc.HasExited) {
    Write-Host "应用已退出，退出码 0x$('{0:X8}' -f $proc.ExitCode)" -ForegroundColor Red
    exit 1
} else {
    Write-Host "应用运行中，界面可正常访问 (UIA 树可枚举)" -ForegroundColor Green
    exit 0
}
