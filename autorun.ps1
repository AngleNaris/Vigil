#Requires -Version 7.0
# autorun.ps1 - SystemAutorun 统一管理 CLI
#
# 用法：
#   pwsh C:\_my\_systemautorun\autorun.ps1 <command> [args] [-Json]
#
# 命令：
#   list                    列出 YAML 中所有任务及当前状态
#   status <name>           单项详情
#   scan                    扫描系统所有自启动项（不只 YAML 中的）
#   doctor                  系统环境检查
#   logs <name> [-Tail N]   查看日志
#   -Json                   以 JSON 输出（供 GUI 解析）
#   enable/disable/start/stop/restart/apply/install/uninstall

[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string]$Command,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)] [string[]]$Rest,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
# 在 -Json 模式下输出 UTF-8，避免中文乱码
if ($Json) {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding            = [System.Text.Encoding]::UTF8
}
$ProjectRoot = $PSScriptRoot
$global:AutorunProjectRoot = $ProjectRoot
. (Join-Path $ProjectRoot 'lib\config.ps1')
. (Join-Path $ProjectRoot 'lib\health.ps1')
. (Join-Path $ProjectRoot 'lib\logger.ps1')
. (Join-Path $ProjectRoot 'lib\scan.ps1')
. (Join-Path $ProjectRoot 'lib\tasks.ps1')
. (Join-Path $ProjectRoot 'lib\scheduler.ps1')
. (Join-Path $ProjectRoot 'lib\startup.ps1')
. (Join-Path $ProjectRoot 'lib\json-command.ps1')

function Write-SectionHeader {
    param([string]$Title)
    if ($Json) { return }
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkGray
    Write-Host " $Title" -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkGray
}

function Format-StatusBadge {
    param([bool]$Ok, [string]$Text = $(if ($Ok) { 'OK ' } else { 'FAIL' }))
    if ($Ok) { return "[$Text]" }
    return "[$Text]"
}

function Emit-Json {
    param($Data)
    $Data | ConvertTo-Json -Depth 12 -Compress
}

# ─────────── list ────────────────────────────────────────────

function Invoke-CommandList {
    param($Config)
    Write-SectionHeader "Tasks in autorun.yaml ($($Config.tasks.Count) total)"
    $rows = @(Get-ListData -Config $Config)
    if ($Json) { Emit-Json @($rows); return }

    $rows | Select-Object @{N='Status';E={"[$($_.status)]"}},
        Name, Type, Distro, Reason, Detail | Format-Table -AutoSize
}

# ─────────── status ──────────────────────────────────────────

function Invoke-CommandStatus {
    param($Config, [string]$Name)
    $data = $null
    try {
        $data = Get-StatusData -Config $Config -Name $Name
    } catch {
        if ($Json) { Emit-Json @{ error = $_.Exception.Message }; exit 1 }
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host "Available: $(($Config.tasks | ForEach-Object { $_.name }) -join ', ')"
        exit 1
    }
    if ($Json) { Emit-Json $data; return }

    $t = $data.task
    Write-SectionHeader "Status: $($t.name)"
    Write-Host "Description : $($t.description)"
    Write-Host "Type        : $($t.type)"
    Write-Host "Enabled     : $($t.enabled)"
    switch ($t.type) {
        'windows' {
            Write-Host "Exec        : $($t.exec)"
            if ($t.args)       { Write-Host "Args        : $($t.args -join ' ')" }
            if ($t.working_dir){ Write-Host "WorkingDir  : $($t.working_dir)" }
        }
        'wsl_boot'    { Write-Host "Distro      : $($t.distro)" }
        'wsl_systemd' {
            Write-Host "Distro      : $($t.distro)"
            Write-Host "Service     : $($t.service)"
        }
    }
    if ($t.depends_on) { Write-Host "Depends on  : ($($t.depends_on -join ', '))" }
    Write-Host "StartupDelay: $($t.startup_delay)s"

    Write-SectionHeader 'Health Check'
    $color = if ($data.health.healthy) { 'Green' } else { 'Yellow' }
    Write-Host "Healthy : $($data.health.healthy)" -ForegroundColor $color
    Write-Host "Reason  : $($data.health.reason)"
    Write-Host "Detail  : $($data.health.detail)"

    if ($t.type -eq 'windows') {
        Write-SectionHeader 'Process'
        if ($data.processes) {
            $data.processes | Select-Object @{N='Id';E={$_.pid}},
                @{N='ProcessName';E={$_.name}},
                @{N='StartTime';E={if($_.start_time){$_.start_time}}},
                @{N='Memory(MB)';E={$_.memory_mb}} |
                Format-Table -AutoSize
        } else {
            Write-Host 'Not running.' -ForegroundColor Yellow
        }
    }

    Write-SectionHeader 'Recent Log'
    if ($data.recent_logs) {
        $data.recent_logs | ForEach-Object { Write-Host "  $_" }
    } else {
        Write-Host '  (no log yet)'
    }
}

# ─────────── scan ────────────────────────────────────────────

function Invoke-CommandScan {
    param($Config)
    $data = Get-ScanData -Config $Config
    if ($Json) { Emit-Json $data; return }

    Write-SectionHeader 'All auto-startup entries on this system'
    $rows = $data.entries | ForEach-Object {
        [pscustomobject]@{
            Managed = if ($_.managed) { '*' } else { '' }
            Source  = $_.source
            Name    = $_.name
            Target  = $_.target
            Args    = $_.args
        }
    }
    $rows | Format-Table -AutoSize -Wrap
    Write-Host ''
    Write-Host "Total: $($data.total) entries | Managed by autorun.yaml: $($data.managed)"
}

# ─────────── doctor ──────────────────────────────────────────

function Invoke-CommandDoctor {
    param($Config)
    $data = Get-DoctorData -Config $Config
    if ($Json) { Emit-Json $data; return }

    Write-SectionHeader 'System Environment Check'
    $data.checks | ForEach-Object {
        $badge = Format-StatusBadge -Ok $_.ok
        $color = if ($_.ok) { 'Green' } else { 'Red' }
        Write-Host ("  {0,-35} {1,-7} {2}" -f $_.check, $badge, $_.detail) -ForegroundColor $color
    }
    Write-Host ''
    if ($data.failed -gt 0) {
        Write-Host "FAILED: $($data.failed) check(s) need attention." -ForegroundColor Red
    } else {
        Write-Host 'All checks passed.' -ForegroundColor Green
    }
}

# ─────────── logs ────────────────────────────────────────────

function Invoke-CommandLogs {
    param($Config, [string]$Name, [int]$Tail = 50)
    $t = Get-TaskByName -Config $Config -Name $Name
    if (-not $t) {
        if ($Json) { Emit-Json @{ error = "Task not found: $Name" }; exit 1 }
        Write-Host "Task not found: $Name" -ForegroundColor Red
        exit 1
    }
    $logs = @(Read-TaskLog -TaskName $Name -Tail $Tail)
    if ($Json) {
        Emit-Json @{ name = $Name; tail = $Tail; lines = @($logs) }
        return
    }
    if (-not $logs) {
        Write-Host "No log file yet for '$Name'."
        return
    }
    $logs | ForEach-Object { Write-Host $_ }
}

# ─────────── 任务控制与配置管理 ───────────────────────────────

function Invoke-CommandStart {
    param($Config, [string]$Name)
    $t = Get-TaskByName -Config $Config -Name $Name
    if (-not $t) {
        if ($Json) { Emit-Json @{ error = "Task not found: $Name" }; exit 1 }
        Write-Host "Task not found: $Name" -ForegroundColor Red
        exit 1
    }
    if ($Json) {
        $ok = Start-Task -Task $t
        Emit-Json @{ name = $Name; started = $ok; task = (Get-ActionTaskSnapshot -Task $t) }
        return
    }
    Write-SectionHeader "Start: $($t.name)"
    $ok = Start-Task -Task $t
    if ($ok) { Write-Host "Started: $($t.name)" -ForegroundColor Green }
    else     { Write-Host "Failed to start: $($t.name)" -ForegroundColor Red; exit 1 }
}

function Invoke-CommandStop {
    param($Config, [string]$Name)
    $t = Get-TaskByName -Config $Config -Name $Name
    if (-not $t) {
        if ($Json) { Emit-Json @{ error = "Task not found: $Name" }; exit 1 }
        Write-Host "Task not found: $Name" -ForegroundColor Red
        exit 1
    }
    if ($Json) {
        $ok = Stop-Task -Task $t
        Emit-Json @{ name = $Name; stopped = $ok; task = (Get-ActionTaskSnapshot -Task $t) }
        return
    }
    Write-SectionHeader "Stop: $($t.name)"
    $ok = Stop-Task -Task $t
    if ($ok) { Write-Host "Stopped: $($t.name)" -ForegroundColor Green }
    else     { Write-Host "Failed to stop: $($t.name)" -ForegroundColor Red; exit 1 }
}

function Invoke-CommandRestart {
    param($Config, [string]$Name)
    $t = Get-TaskByName -Config $Config -Name $Name
    if (-not $t) {
        if ($Json) { Emit-Json @{ error = "Task not found: $Name" }; exit 1 }
        Write-Host "Task not found: $Name" -ForegroundColor Red
        exit 1
    }
    if ($Json) {
        $ok = Restart-Task -Task $t
        Emit-Json @{ name = $Name; restarted = $ok; task = (Get-ActionTaskSnapshot -Task $t) }
        return
    }
    Write-SectionHeader "Restart: $($t.name)"
    $ok = Restart-Task -Task $t
    if ($ok) { Write-Host "Restarted: $($t.name)" -ForegroundColor Green }
    else     { Write-Host "Failed to restart: $($t.name)" -ForegroundColor Red; exit 1 }
}

function Invoke-CommandEnable {
    param($Config, [string]$Name)
    $t = Get-TaskByName -Config $Config -Name $Name
    if (-not $t) {
        if ($Json) { Emit-Json @{ error = "Task not found: $Name" }; exit 1 }
        Write-Host "Task not found: $Name" -ForegroundColor Red
        exit 1
    }
    if ([bool]$t.enabled) {
        if ($Json) {
            Emit-Json @{ name = $Name; enabled = $true; message = 'already enabled'; task = (Get-ActionTaskSnapshot -Task $t) }
            return
        }
        Write-Host "Task '$Name' is already enabled." -ForegroundColor Yellow
        return
    }
    Set-TaskOverride -Name $Name -Enabled $true
    $t.enabled = $true
    if ($Json) {
        Emit-Json @{ name = $Name; enabled = $true; override_applied = $true; task = (Get-ActionTaskSnapshot -Task $t) }
        return
    }
    Write-Host "Enabled: $Name (override applied)" -ForegroundColor Green
    Write-Host "Will be auto-launched on next AtStartup if Boot Task is installed." -ForegroundColor DarkGray
}

function Invoke-CommandDisable {
    param($Config, [string]$Name)
    $t = Get-TaskByName -Config $Config -Name $Name
    if (-not $t) {
        if ($Json) { Emit-Json @{ error = "Task not found: $Name" }; exit 1 }
        Write-Host "Task not found: $Name" -ForegroundColor Red
        exit 1
    }
    if (-not [bool]$t.enabled) {
        if ($Json) {
            Emit-Json @{ name = $Name; enabled = $false; message = 'already disabled'; task = (Get-ActionTaskSnapshot -Task $t) }
            return
        }
        Write-Host "Task '$Name' is already disabled." -ForegroundColor Yellow
        return
    }
    Set-TaskOverride -Name $Name -Enabled $false
    $t.enabled = $false
    if ($Json) {
        Emit-Json @{ name = $Name; enabled = $false; override_applied = $true; task = (Get-ActionTaskSnapshot -Task $t) }
        return
    }
    Write-Host "Disabled: $Name (override applied)" -ForegroundColor Green
    Write-Host "Currently running instance (if any) is NOT stopped. Use 'autorun.ps1 stop $Name' to stop now." -ForegroundColor DarkGray
}

function Invoke-CommandInstall {
    param($Config)
    if ($Json) {
        try {
            $result = Register-AutorunBootTask
            Emit-Json @{ installed = $true; task = $result }
        } catch {
            Emit-Json @{ installed = $false; error = $_.Exception.Message }
            exit 1
        }
        return
    }
    Write-SectionHeader "Install: SystemAutorun\Boot Task"
    try {
        $result = Register-AutorunBootTask
        Write-Host ""
        $result | Format-List
    } catch {
        Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandUninstall {
    param($Config)
    if ($Json) {
        try {
            Unregister-AutorunBootTask
            Emit-Json @{ uninstalled = $true }
        } catch {
            Emit-Json @{ uninstalled = $false; error = $_.Exception.Message }
            exit 1
        }
        return
    }
    Write-SectionHeader "Uninstall: SystemAutorun\Boot Task"
    try {
        Unregister-AutorunBootTask
    } catch {
        Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandStartBoot {
    param($Config)
    if ($Json) {
        try {
            Start-BootTaskNow
            Emit-Json @{ triggered = $true }
        } catch {
            Emit-Json @{ triggered = $false; error = $_.Exception.Message }
            exit 1
        }
        return
    }
    Write-SectionHeader "Start Boot Task now"
    try { Start-BootTaskNow } catch {
        Write-Host "Trigger failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandApply {
    param($Config)
    if ($Json) {
        try {
            $result = Invoke-AutorunApply -Config $Config
            Emit-Json @{
                applied             = $true
                boot_task           = $result.boot_task
                boot_task_installed = [bool]$result.boot_task_installed
                enabled_tasks       = $result.enabled_tasks
                disabled_tasks      = $result.disabled_tasks
            }
        } catch {
            Emit-Json @{ applied = $false; error = $_.Exception.Message }
            exit 1
        }
        return
    }
    Write-SectionHeader "Apply: synchronize config"
    Invoke-AutorunApply -Config $Config
}

# ─────────── Startup entries / GUI self-start / task authoring ────

function Invoke-CommandStartupList {
    param($Config)
    $data = Get-StartupManagementData -Config $Config
    if ($Json) { Emit-Json $data; return }

    Write-SectionHeader 'Windows startup entries'
    $data.entries | ForEach-Object {
        [pscustomobject]@{
            State   = if ($_.disabled) { 'DISABLED' } else { 'ACTIVE' }
            Managed = if ($_.managed) { '*' } else { '' }
            Source  = $_.source
            Name    = $_.name
            Target  = $_.target
            Args    = $_.args
        }
    } | Format-Table -AutoSize -Wrap
}

function Invoke-CommandStartupDisable {
    param([string[]]$StartupArgs)
    $source = $StartupArgs[0]
    $name = $StartupArgs[1]
    $origin = if ($StartupArgs.Count -ge 3) { $StartupArgs[2] } else { $null }
    if (-not $source -or -not $name) {
        if ($Json) { Emit-Json @{ error = 'startup-disable requires <source> <name> [origin]' }; exit 1 }
        Write-Host 'Usage: startup-disable <source> <name> [origin]' -ForegroundColor Red
        exit 1
    }
    try {
        $result = Disable-StartupEntry -Source $source -Name $name -Origin $origin
        if ($Json) { Emit-Json $result; return }
        Write-Host "Disabled startup entry: $source / $name" -ForegroundColor Green
    } catch {
        if ($Json) { Emit-Json @{ disabled = $false; error = $_.Exception.Message }; exit 1 }
        Write-Host "Disable failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandStartupRestore {
    param([string]$Id)
    if (-not $Id) {
        if ($Json) { Emit-Json @{ error = 'startup-restore requires <backup-id>' }; exit 1 }
        Write-Host 'Usage: startup-restore <backup-id>' -ForegroundColor Red
        exit 1
    }
    try {
        $result = Restore-StartupEntry -Id $Id
        if ($Json) { Emit-Json $result; return }
        Write-Host "Restored startup entry: $($result.source) / $($result.name)" -ForegroundColor Green
    } catch {
        if ($Json) { Emit-Json @{ restored = $false; error = $_.Exception.Message }; exit 1 }
        Write-Host "Restore failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandSelfStartup {
    param([string]$Action)
    try {
        switch ($Action) {
            'status'  { $result = Get-SelfStartupStatus }
            'enable'  { $result = Enable-SelfStartup }
            'disable' { $result = Disable-SelfStartup }
            default {
                if ($Json) { Emit-Json @{ error = 'self-startup requires status|enable|disable' }; exit 1 }
                Write-Host 'Usage: self-startup status|enable|disable' -ForegroundColor Red
                exit 1
            }
        }
        if ($Json) { Emit-Json $result; return }
        $result | Format-List
    } catch {
        if ($Json) { Emit-Json @{ error = $_.Exception.Message }; exit 1 }
        Write-Host "Self-startup failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandAddWindowsTask {
    param($Config, [string[]]$CommandArgs)
    if (-not $CommandArgs -or -not $CommandArgs[0]) {
        if ($Json) { Emit-Json @{ error = 'add-windows requires a JSON payload argument' }; exit 1 }
        Write-Host 'Usage: add-windows <json-payload>' -ForegroundColor Red
        exit 1
    }
    try {
        $result = Add-WindowsTaskToConfigFile -Config $Config -PayloadJson $CommandArgs[0]
        if ($Json) { Emit-Json $result; return }
        Write-Host "Added Windows task: $($result.name)" -ForegroundColor Green
    } catch {
        if ($Json) { Emit-Json @{ added = $false; error = $_.Exception.Message }; exit 1 }
        Write-Host "Add failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandRemoveTask {
    param($Config, [string]$Name)
    if (-not $Name) {
        if ($Json) { Emit-Json @{ error = 'remove-task requires <name>' }; exit 1 }
        Write-Host 'Usage: remove-task <name>' -ForegroundColor Red
        exit 1
    }
    try {
        if (-not (Get-TaskByName -Config $Config -Name $Name)) {
            throw "Task not found: $Name"
        }
        $result = Remove-TaskFromConfigFile -Name $Name
        if ($Json) { Emit-Json $result; return }
        Write-Host "Removed task from list: $Name" -ForegroundColor Green
        Write-Host 'Note: running process was not stopped. Use stop if needed.' -ForegroundColor DarkGray
    } catch {
        if ($Json) { Emit-Json @{ removed = $false; error = $_.Exception.Message }; exit 1 }
        Write-Host "Remove failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-CommandStartMissing {
    param($Config)
    $result = Get-StartMissingData -Config $Config
    if ($Json) { Emit-Json $result; return }
    Write-SectionHeader 'Start missing enabled tasks'
    $result.summary | Format-List
    if ($result.started) { Write-Host "Started: $(($result.started | ForEach-Object name) -join ', ')" -ForegroundColor Green }
    if ($result.failed)  { Write-Host "Failed:  $(($result.failed  | ForEach-Object name) -join ', ')" -ForegroundColor Red }
}

# ─────────── 入口 ────────────────────────────────────────────

try {
    $config = Import-AutorunConfig
    Initialize-Logger -LogDir $config.settings.log_dir

    switch ($Command) {
        $null   { Write-Host "Usage: autorun.ps1 <command> [args] [-Json]"; Write-Host 'Commands: list, status <name>, scan, doctor, logs <name> [-Tail N]' }
        'list'  { Invoke-CommandList    -Config $config }
        'status'{
            if (-not $Rest -or -not $Rest[0]) {
                if ($Json) { Emit-Json @{ error = 'status requires <name>' }; exit 1 }
                Write-Host 'Usage: status <name>' -ForegroundColor Red; exit 1
            }
            Invoke-CommandStatus -Config $config -Name $Rest[0]
        }
        'scan'  { Invoke-CommandScan    -Config $config }
        'doctor'{ Invoke-CommandDoctor  -Config $config }
        'logs'  {
            if (-not $Rest -or -not $Rest[0]) {
                if ($Json) { Emit-Json @{ error = 'logs requires <name>' }; exit 1 }
                Write-Host 'Usage: logs <name> [-Tail N]' -ForegroundColor Red; exit 1
            }
            $tail = 50
            if ($Rest.Count -ge 3 -and $Rest[1] -eq '-Tail') { $tail = [int]$Rest[2] }
            Invoke-CommandLogs -Config $config -Name $Rest[0] -Tail $tail
        }
        'start' {
            if (-not $Rest -or -not $Rest[0]) {
                if ($Json) { Emit-Json @{ error = 'start requires <name>' }; exit 1 }
                Write-Host 'Usage: start <name>' -ForegroundColor Red; exit 1
            }
            Invoke-CommandStart -Config $config -Name $Rest[0]
        }
        'stop' {
            if (-not $Rest -or -not $Rest[0]) {
                if ($Json) { Emit-Json @{ error = 'stop requires <name>' }; exit 1 }
                Write-Host 'Usage: stop <name>' -ForegroundColor Red; exit 1
            }
            Invoke-CommandStop -Config $config -Name $Rest[0]
        }
        'restart' {
            if (-not $Rest -or -not $Rest[0]) {
                if ($Json) { Emit-Json @{ error = 'restart requires <name>' }; exit 1 }
                Write-Host 'Usage: restart <name>' -ForegroundColor Red; exit 1
            }
            Invoke-CommandRestart -Config $config -Name $Rest[0]
        }
        'enable' {
            if (-not $Rest -or -not $Rest[0]) {
                if ($Json) { Emit-Json @{ error = 'enable requires <name>' }; exit 1 }
                Write-Host 'Usage: enable <name>' -ForegroundColor Red; exit 1
            }
            Invoke-CommandEnable -Config $config -Name $Rest[0]
        }
        'disable' {
            if (-not $Rest -or -not $Rest[0]) {
                if ($Json) { Emit-Json @{ error = 'disable requires <name>' }; exit 1 }
                Write-Host 'Usage: disable <name>' -ForegroundColor Red; exit 1
            }
            Invoke-CommandDisable -Config $config -Name $Rest[0]
        }
        'install'   { Invoke-CommandInstall   -Config $config }
        'uninstall' { Invoke-CommandUninstall -Config $config }
        'apply'     { Invoke-CommandApply     -Config $config }
        'start-boot'{ Invoke-CommandStartBoot -Config $config }
        'startup-list'    { Invoke-CommandStartupList -Config $config }
        'startup-disable' { Invoke-CommandStartupDisable -StartupArgs $Rest }
        'startup-restore' { Invoke-CommandStartupRestore -Id $Rest[0] }
        'self-startup'    { Invoke-CommandSelfStartup -Action $Rest[0] }
        'add-windows'     { Invoke-CommandAddWindowsTask -Config $config -CommandArgs $Rest }
        'remove-task'     { Invoke-CommandRemoveTask -Config $config -Name $Rest[0] }
        'start-missing'   { Invoke-CommandStartMissing -Config $config }
        default {
            if ($Json) { Emit-Json @{ error = "Unknown command: $Command" }; exit 2 }
            Write-Host "Unknown command: $Command" -ForegroundColor Red
            Write-Host "Available: list, status, scan, doctor, logs, start, stop, restart, enable, disable, install, uninstall, apply, start-boot, startup-list, startup-disable, startup-restore, self-startup, add-windows, start-missing"
            exit 2
        }
    }
} catch {
    if ($Json) { Emit-Json @{ error = $_.Exception.Message }; exit 1 }
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
