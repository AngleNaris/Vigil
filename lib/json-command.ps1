#Requires -Version 7.0
# lib/json-command.ps1 - JSON 命令实现（CLI -Json 与常驻 worker 共用）
# 依赖：已 dot-source config/health/logger/scan/tasks/scheduler/startup

# 由 autorun.ps1 / autorun-worker.ps1 在入口处设置，供并行 runspace 定位 lib
if (-not $global:AutorunProjectRoot) {
    $global:AutorunProjectRoot = Split-Path $PSScriptRoot -Parent
}

function Get-TaskListRow {
    param($Task)

    $healthy = $false
    $reason  = ''
    $detail  = ''
    try {
        $r = Test-TaskHealth -Task $Task -Quick
        $healthy = [bool]$r.healthy
        $reason  = $r.reason
        $detail  = $r.detail
    } catch {
        $reason = 'health-error'
        $detail = $_.Exception.Message
    }

    $badge = if (-not $Task.enabled) { 'OFF' } elseif ($healthy) { 'OK' } else { 'FAIL' }

    return [pscustomobject]@{
        status        = $badge
        healthy       = $healthy
        enabled       = [bool]$Task.enabled
        name          = $Task.name
        type          = $Task.type
        description   = $Task.description
        reason        = $reason
        detail        = $detail
        startup_delay = [int]$Task.startup_delay
        exec          = $Task.exec
        args          = @($Task.args       | Where-Object { $_ })
        working_dir   = $Task.working_dir
        distro        = $Task.distro
        service       = $Task.service
        depends_on    = @($Task.depends_on | Where-Object { $_ })
    }
}

function Initialize-SystemdHealthPrefetch {
    param([object[]]$Tasks)

    # 按 distro 合并 systemctl 探测：每个 distro 只 spawn 一次 wsl.exe
    # 已在 TTL 内的缓存直接跳过，避免每次刷新都打 wsl
    $ttlSec = if ($script:_SystemdActiveCacheTtlSec) { [int]$script:_SystemdActiveCacheTtlSec } else { 5 }
    $now = [datetime]::Now

    $items = foreach ($t in $Tasks) {
        $healthType = if ($t.health) { [string]$t.health.type } else { '' }
        if ($healthType -ne 'systemd_active') { continue }
        $distro  = if ($t.health.distro)  { [string]$t.health.distro }  else { [string]$t.distro }
        $service = if ($t.health.service) { [string]$t.health.service } else { [string]$t.service }
        if (-not $distro -or -not $service) { continue }
        $cacheKey = "${distro}|${service}"
        $cached = $script:_SystemdActiveCache[$cacheKey]
        if ($cached -and ($now - $cached.at).TotalSeconds -lt $ttlSec) { continue }
        [pscustomobject]@{ Distro = $distro; Service = $service }
    }
    $groups = @($items | Group-Object Distro)
    if ($groups.Count -eq 0) { return }

    foreach ($g in $groups) {
        $distro = [string]$g.Name
        $state = Get-WslDistroState -Distro $distro
        if (-not $state.Running) { continue }

        $services = @($g.Group | ForEach-Object { [string]$_.Service } | Select-Object -Unique)
        if ($services.Count -eq 0) { continue }

        $parts = foreach ($svc in $services) {
            $safe = $svc.Replace("'", "'\''")
            "echo ${svc}:`$(systemctl is-active --quiet '$safe' && echo OK || echo FAIL)"
        }
        $cmd = $parts -join '; '
        try {
            $out = Invoke-WslCommand -Distro $distro -Command $cmd -TimeoutSec 12
        } catch {
            continue
        }
        foreach ($line in ($out -split "`r?`n")) {
            if ($line -match '^(?<svc>[^:]+):(?<st>OK|FAIL)\s*$') {
                $svcName = $Matches['svc'].Trim()
                $ok = $Matches['st'] -eq 'OK'
                $cacheKey = "${distro}|${svcName}"
                $script:_SystemdActiveCache[$cacheKey] = @{
                    at     = [datetime]::Now
                    result = [ordered]@{ ok = $ok; detail = "${distro}:${svcName}" }
                }
            }
        }
    }
}

function Get-ListData {
    param($Config)
    Initialize-Logger -LogDir $Config.settings.log_dir

    $tasks = @($Config.tasks)
    if ($tasks.Count -eq 0) { return @() }

    # 预取可共享结果，减少 list 内重复的 wsl.exe 调用
    # 仅在 WSL 运行列表缓存过期时刷新（Get-WslRunningList 本身无缓存）
    try {
        if (-not $script:_WslRunningCacheAt -or
            ([datetime]::Now - $script:_WslRunningCacheAt).TotalSeconds -gt 5) {
            $script:_WslRunningCache = @(Get-WslRunningList)
            $script:_WslRunningCacheAt = [datetime]::Now
        }
    } catch {}
    try { Initialize-SystemdHealthPrefetch -Tasks $tasks } catch {}

    # process/tcp 本地检查；WSL/systemd 命中上面的预取缓存
    return @(foreach ($t in $tasks) { Get-TaskListRow -Task $t })
}

function Get-ActionTaskSnapshot {
    param($Task)
    try {
        return (Get-TaskListRow -Task $Task)
    } catch {
        return $null
    }
}

function Get-StatusData {
    param($Config, [string]$Name)
    $t = Get-TaskByName -Config $Config -Name $Name
    if (-not $t) { throw "Task not found: $Name" }

    $healthResult = $null
    try {
        $healthResult = Test-TaskHealth -Task $t
    } catch {
        $healthResult = [ordered]@{ healthy = $false; reason = 'health-error'; detail = $_.Exception.Message }
    }

    $processes = @()
    if ($t.type -eq 'windows' -and $t.exec) {
        $matches = @(Get-WindowsTaskProcesses -Task $t -IncludeDetails)
        if ($matches) {
            $processes = foreach ($entry in $matches) {
                $proc = $entry.Process
                $memory = $null
                $start = $null
                if ($proc) {
                    try { $memory = [math]::Round($proc.WorkingSet64 / 1MB, 1) } catch {}
                    try { $start = $proc.StartTime.ToString('o') } catch {}
                }
                [pscustomobject]@{
                    pid             = $entry.ProcessId
                    parent_pid      = $entry.ParentProcessId
                    name            = $entry.Name
                    executable_path = $entry.ExecutablePath
                    command_line    = $entry.CommandLine
                    memory_mb       = $memory
                    start_time      = $start
                }
            }
        }
    }

    $logs = @(Read-TaskLog -TaskName $t.name -Tail 10)

    return [pscustomobject]@{
        task         = $t
        health       = $healthResult
        processes    = @($processes)
        recent_logs  = @($logs)
    }
}

function Get-ScanData {
    param($Config)
    $entries = Get-AutorunEntries
    $compared = Compare-EntriesWithConfig -Entries $entries -Config $Config
    return [pscustomobject]@{
        total     = $entries.Count
        managed   = ($compared | Where-Object Managed).Count
        entries   = @($compared | ForEach-Object {
            [pscustomobject]@{
                managed = [bool]$_.Managed
                task    = $_.Task
                source  = $_.Entry.Source
                name    = $_.Entry.Name
                target  = $_.Entry.Target
                args    = $_.Entry.Arguments
                origin  = $_.Entry.Origin
            }
        })
    }
}

function Get-DoctorData {
    param($Config)
    $checks = @()

    $wslExe = Get-Command wsl.exe -ErrorAction SilentlyContinue
    $checks += [pscustomobject]@{
        check  = 'wsl.exe'
        ok     = [bool]$wslExe
        detail = if ($wslExe) { $wslExe.Source } else { 'not found' }
    }

    $ubuntu = Get-WslDistroState -Distro 'Ubuntu'
    $checks += [pscustomobject]@{
        check  = 'WSL Ubuntu'
        ok     = [bool]$ubuntu.Running
        detail = "State=$($ubuntu.State) Version=$($ubuntu.Version)"
    }

    if ($ubuntu.Running) {
        $sdPid1 = (Invoke-WslCommand -Distro 'Ubuntu' -Command 'ps -p 1 -o comm= 2>/dev/null').Trim()
        $checks += [pscustomobject]@{
            check  = 'systemd (PID 1)'
            ok     = $sdPid1 -match 'systemd'
            detail = "PID 1 = $sdPid1"
        }
    } else {
        $checks += [pscustomobject]@{
            check  = 'systemd (PID 1)'
            ok     = $true
            detail = 'SKIPPED (WSL not running; will check on first start)'
        }
    }

    if ($ubuntu.Running) {
        $svcExists = Invoke-WslCommand -Distro 'Ubuntu' -Command "systemctl list-unit-files --type=service | grep -F 'hermes-gateway.service' | wc -l"
        $servicePresent = $svcExists.Trim() -ne '0'
        $checks += [pscustomobject]@{
            check  = 'hermes-gateway.service'
            ok     = $servicePresent
            detail = if ($servicePresent) { 'present' } else { 'MISSING' }
        }
    } else {
        $checks += [pscustomobject]@{
            check  = 'hermes-gateway.service'
            ok     = $true
            detail = 'SKIPPED (WSL not running; will check on first start)'
        }
    }

    $svc = Get-Service -Name 'Schedule' -ErrorAction SilentlyContinue
    $checks += [pscustomobject]@{
        check  = 'Task Scheduler service'
        ok     = ($svc -and $svc.Status -eq 'Running')
        detail = if ($svc) { [string]$svc.Status } else { 'not installed' }
    }

    $mod = Get-Module -ListAvailable powershell-yaml -ErrorAction SilentlyContinue | Select-Object -First 1
    $checks += [pscustomobject]@{
        check  = 'powershell-yaml module'
        ok     = [bool]$mod
        detail = if ($mod) { "v$($mod.Version)" } else { 'MISSING' }
    }

    foreach ($t in $Config.tasks) {
        if ($t.type -eq 'windows') {
            $resolvedExec = Resolve-WindowsExecutable -Executable $t.exec
            $exists = [bool]$resolvedExec
            $checks += [pscustomobject]@{
                check  = "Exec path: $($t.name)"
                ok     = $exists
                detail = if ($resolvedExec) { $resolvedExec } else { $t.exec }
            }
        }
    }

    return [pscustomobject]@{
        checks   = $checks
        failed   = ($checks | Where-Object { -not $_.ok }).Count
        passed   = ($checks | Where-Object { $_.ok }).Count
        all_ok   = (($checks | Where-Object { -not $_.ok }).Count -eq 0)
    }
}

function Get-StartupManagementData {
    param($Config)
    $entries = Get-AutorunEntries
    $compared = Compare-EntriesWithConfig -Entries $entries -Config $Config
    $active = $compared | ForEach-Object {
        [pscustomobject]@{
            managed        = [bool]$_.Managed
            task           = $_.Task
            source         = $_.Entry.Source
            name           = $_.Entry.Name
            target         = $_.Entry.Target
            args           = $_.Entry.Arguments
            working_dir    = $_.Entry.WorkingDir
            origin         = $_.Entry.Origin
            raw_command    = $_.Entry.RawCommand
            disabled       = $false
            requires_admin = Test-StartupSourceRequiresAdmin -Source $_.Entry.Source
            can_modify     = (-not (Test-StartupSourceRequiresAdmin -Source $_.Entry.Source)) -or (Test-StartupElevated)
        }
    }
    $disabled = Get-DisabledStartupEntries | ForEach-Object {
        [pscustomobject]@{
            managed        = $false
            task           = $null
            source         = $_.source
            name           = $_.name
            target         = $_.target
            args           = $_.args
            working_dir    = $null
            origin         = $_.origin
            raw_command    = $null
            disabled       = $true
            backup_id      = $_.id
            disabled_at    = $_.disabled_at
            requires_admin = Test-StartupSourceRequiresAdmin -Source $_.source
            can_modify     = (-not (Test-StartupSourceRequiresAdmin -Source $_.source)) -or (Test-StartupElevated)
        }
    }

    return [pscustomobject]@{
        total      = @($active).Count
        managed    = @($active | Where-Object managed).Count
        disabled   = @($disabled).Count
        elevated   = Test-StartupElevated
        self_start = Get-SelfStartupStatus
        entries    = @($active) + @($disabled)
    }
}

function Get-StartMissingData {
    param($Config)
    $enabled = @($Config.tasks | Where-Object { $_.enabled })
    $started = @()
    $skipped = @()
    $failed = @()

    foreach ($t in ($enabled | Sort-Object { [int]$_.startup_delay })) {
        $health = $null
        try {
            $health = Test-TaskHealth -Task $t -Quick
        } catch {
            $health = [ordered]@{ healthy = $false; reason = 'health-error'; detail = $_.Exception.Message }
        }
        if ([bool]$health.healthy) {
            $skipped += [pscustomobject]@{ name = $t.name; reason = $health.reason; detail = $health.detail }
            continue
        }
        $ok = Start-Task -Task $t
        $row = [pscustomobject]@{ name = $t.name; ok = [bool]$ok; reason = $health.reason; detail = $health.detail }
        if ($ok) { $started += $row } else { $failed += $row }
    }

    return [pscustomobject]@{
        started = $started
        skipped = $skipped
        failed  = $failed
        summary = [pscustomobject]@{
            started = $started.Count
            skipped = $skipped.Count
            failed  = $failed.Count
        }
    }
}

# 统一 JSON 命令入口：成功返回数据对象，失败 throw（由 CLI/worker 包装 error）
function Invoke-AutorunJsonCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Command,
        [string[]]$Rest = @(),
        [Parameter(Mandatory)] $Config
    )

    switch ($Command) {
        'list' {
            return ,@(Get-ListData -Config $Config)
        }
        'status' {
            if (-not $Rest -or -not $Rest[0]) { throw 'status requires <name>' }
            return (Get-StatusData -Config $Config -Name $Rest[0])
        }
        'scan' {
            return (Get-ScanData -Config $Config)
        }
        'doctor' {
            return (Get-DoctorData -Config $Config)
        }
        'logs' {
            if (-not $Rest -or -not $Rest[0]) { throw 'logs requires <name>' }
            $tail = 50
            if ($Rest.Count -ge 3 -and $Rest[1] -eq '-Tail') { $tail = [int]$Rest[2] }
            $t = Get-TaskByName -Config $Config -Name $Rest[0]
            if (-not $t) { throw "Task not found: $($Rest[0])" }
            return [pscustomobject]@{
                name  = $Rest[0]
                tail  = $tail
                lines = @((Read-TaskLog -TaskName $Rest[0] -Tail $tail))
            }
        }
        'start' {
            if (-not $Rest -or -not $Rest[0]) { throw 'start requires <name>' }
            $t = Get-TaskByName -Config $Config -Name $Rest[0]
            if (-not $t) { throw "Task not found: $($Rest[0])" }
            $ok = Start-Task -Task $t
            return [pscustomobject]@{
                name    = $Rest[0]
                started = [bool]$ok
                task    = (Get-ActionTaskSnapshot -Task $t)
            }
        }
        'stop' {
            if (-not $Rest -or -not $Rest[0]) { throw 'stop requires <name>' }
            $t = Get-TaskByName -Config $Config -Name $Rest[0]
            if (-not $t) { throw "Task not found: $($Rest[0])" }
            $ok = Stop-Task -Task $t
            return [pscustomobject]@{
                name    = $Rest[0]
                stopped = [bool]$ok
                task    = (Get-ActionTaskSnapshot -Task $t)
            }
        }
        'restart' {
            if (-not $Rest -or -not $Rest[0]) { throw 'restart requires <name>' }
            $t = Get-TaskByName -Config $Config -Name $Rest[0]
            if (-not $t) { throw "Task not found: $($Rest[0])" }
            $ok = Restart-Task -Task $t
            return [pscustomobject]@{
                name      = $Rest[0]
                restarted = [bool]$ok
                task      = (Get-ActionTaskSnapshot -Task $t)
            }
        }
        'enable' {
            if (-not $Rest -or -not $Rest[0]) { throw 'enable requires <name>' }
            $name = $Rest[0]
            $t = Get-TaskByName -Config $Config -Name $name
            if (-not $t) { throw "Task not found: $name" }
            if ([bool]$t.enabled) {
                return [pscustomobject]@{
                    name    = $name
                    enabled = $true
                    message = 'already enabled'
                    task    = (Get-ActionTaskSnapshot -Task $t)
                }
            }
            Set-TaskOverride -Name $name -Enabled $true
            $t.enabled = $true
            return [pscustomobject]@{
                name             = $name
                enabled          = $true
                override_applied = $true
                task             = (Get-ActionTaskSnapshot -Task $t)
            }
        }
        'disable' {
            if (-not $Rest -or -not $Rest[0]) { throw 'disable requires <name>' }
            $name = $Rest[0]
            $t = Get-TaskByName -Config $Config -Name $name
            if (-not $t) { throw "Task not found: $name" }
            if (-not [bool]$t.enabled) {
                return [pscustomobject]@{
                    name    = $name
                    enabled = $false
                    message = 'already disabled'
                    task    = (Get-ActionTaskSnapshot -Task $t)
                }
            }
            Set-TaskOverride -Name $name -Enabled $false
            $t.enabled = $false
            return [pscustomobject]@{
                name             = $name
                enabled          = $false
                override_applied = $true
                task             = (Get-ActionTaskSnapshot -Task $t)
            }
        }
        'install' {
            $result = Register-AutorunBootTask
            $status = Get-BootTaskStatus
            if (-not $status) {
                throw 'Boot Task register finished but verification failed (task not found).'
            }
            return [pscustomobject]@{
                installed = $true
                task      = $result
                boot_task = $status
            }
        }
        'uninstall' {
            Unregister-AutorunBootTask
            return [pscustomobject]@{
                uninstalled         = $true
                boot_task_installed = [bool](Get-BootTaskStatus)
            }
        }
        'apply' {
            $result = Invoke-AutorunApply -Config $Config
            return [pscustomobject]@{
                applied             = $true
                boot_task           = $result.boot_task
                boot_task_installed = [bool]$result.boot_task_installed
                enabled_tasks       = $result.enabled_tasks
                disabled_tasks      = $result.disabled_tasks
            }
        }
        'start-boot' {
            Start-BootTaskNow
            return [pscustomobject]@{ triggered = $true }
        }
        'startup-list' {
            return (Get-StartupManagementData -Config $Config)
        }
        'startup-disable' {
            $source = if ($Rest.Count -ge 1) { $Rest[0] } else { $null }
            $name = if ($Rest.Count -ge 2) { $Rest[1] } else { $null }
            $origin = if ($Rest.Count -ge 3) { $Rest[2] } else { $null }
            if (-not $source -or -not $name) {
                throw 'startup-disable requires <source> <name> [origin]'
            }
            return (Disable-StartupEntry -Source $source -Name $name -Origin $origin)
        }
        'startup-restore' {
            if (-not $Rest -or -not $Rest[0]) { throw 'startup-restore requires <backup-id>' }
            return (Restore-StartupEntry -Id $Rest[0])
        }
        'self-startup' {
            $action = if ($Rest -and $Rest[0]) { $Rest[0] } else { '' }
            switch ($action) {
                'status'  { return (Get-SelfStartupStatus) }
                'enable'  { return (Enable-SelfStartup) }
                'disable' { return (Disable-SelfStartup) }
                default   { throw 'self-startup requires status|enable|disable' }
            }
        }
        'add-windows' {
            if (-not $Rest -or -not $Rest[0]) { throw 'add-windows requires a JSON payload argument' }
            return (Add-WindowsTaskToConfigFile -Config $Config -PayloadJson $Rest[0])
        }
        'remove-task' {
            if (-not $Rest -or -not $Rest[0]) { throw 'remove-task requires <name>' }
            $name = $Rest[0]
            $t = Get-TaskByName -Config $Config -Name $name
            if (-not $t) { throw "Task not found: $name" }
            return (Remove-TaskFromConfigFile -Name $name)
        }
        'start-missing' {
            return (Get-StartMissingData -Config $Config)
        }
        default {
            throw "Unknown command: $Command"
        }
    }
}
