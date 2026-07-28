#Requires -Version 7.0
# scripts/boot.ps1 - AtStartup 时由 Task Scheduler (SYSTEM 身份) 触发
#
# 工作流：
#   1. 加载 autorun.yaml
#   2. 按 startup_delay 升序排序 enabled 任务
#   3. 任务间的"间隔时间" = current.delay - prev.delay（避免总耗时 = sum(delays)）
#   4. 每个 Start-Task 内部 spawn 进程后立即返回（不等待）
#   5. 写汇总到 logs/boot.log
#
# 注：在 SYSTEM 身份下运行，GUI 程序的窗口在 Session 0 不可见（预期行为）

$ErrorActionPreference = 'Continue'
$ProjectRoot = Split-Path $PSScriptRoot -Parent

. (Join-Path $ProjectRoot 'lib\config.ps1')
. (Join-Path $ProjectRoot 'lib\logger.ps1')
. (Join-Path $ProjectRoot 'lib\health.ps1')
. (Join-Path $ProjectRoot 'lib\tasks.ps1')

function Write-BootLog {
    param([string]$Message)
    $bootLog = Join-Path $ProjectRoot 'logs\boot.log'
    try {
        $dir = Split-Path $bootLog -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Add-Content -Path $bootLog -Value "[$ts] $Message" -Encoding UTF8
    } catch {}
}

Write-BootLog "==== SystemAutorun boot started (PID=$PID, user=$env:USERNAME) ===="

try {
    $config = Import-AutorunConfig
} catch {
    Write-BootLog "FATAL: config load failed: $($_.Exception.Message)"
    exit 1
}

Initialize-Logger -LogDir $config.settings.log_dir

$enabled  = @($config.tasks | Where-Object { $_.enabled })
$disabled = @($config.tasks | Where-Object { -not $_.enabled })

Write-BootLog "Tasks: $($config.tasks.Count) total | $($enabled.Count) enabled | $($disabled.Count) disabled"

if ($enabled.Count -eq 0) {
    Write-BootLog "No enabled tasks; exiting."
    exit 0
}

# 按 startup_delay 升序排序
$sorted = $enabled | Sort-Object { [int]$_.startup_delay }

$prevDelay = 0
$summary = @()
foreach ($t in $sorted) {
    $currentDelay = [int]$t.startup_delay
    $gap = $currentDelay - $prevDelay
    if ($gap -gt 0) {
        Write-BootLog "  sleeping ${gap}s before '$($t.name)' (delay=$currentDelay, prev=$prevDelay)"
        Start-Sleep -Seconds $gap
    }
    $prevDelay = $currentDelay

    $tStart = Get-Date
    Write-BootLog "  >> starting task '$($t.name)' (type=$($t.type))"
    $ok = Start-Task -Task $t
    $dur = ((Get-Date) - $tStart).TotalSeconds

    $summary += [pscustomobject]@{
        Name      = $t.name
        Type      = $t.type
        Result    = if ($ok) { 'OK' } else { 'FAIL' }
        Seconds   = [math]::Round($dur, 1)
    }
    Write-BootLog "  << task '$($t.name)' result=$(if ($ok) { 'OK' } else { 'FAIL' }) (${dur}s)"
}

Write-BootLog "==== SystemAutorun boot finished ===="
Write-BootLog "Summary:"
$summary | ForEach-Object {
    Write-BootLog ("  {0,-25} {1,-12} {2,-5} {3}s" -f $_.Name, $_.Type, $_.Result, $_.Seconds)
}

# 退出码：任一失败返回 1（Task Scheduler 会按 RestartCount 重试）
$failed = ($summary | Where-Object { $_.Result -eq 'FAIL' }).Count
if ($failed -gt 0) {
    Write-BootLog "$failed task(s) failed."
    exit 1
}
exit 0
