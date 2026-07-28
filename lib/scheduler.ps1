#Requires -Version 7.0
# lib/scheduler.ps1 - Task Scheduler CRUD（AtStartup + SYSTEM 身份）
#
# 用法：在 autorun.ps1 中 dot-source
#   . (Join-Path $ProjectRoot 'lib\scheduler.ps1')

# 规范注册：文件夹 \SystemAutorun\ + 任务名 Boot
# 历史版本曾用 TaskName='SystemAutorun\Boot'（无 TaskPath），查找需兼容两者
$BOOT_TASK_NAME = 'Boot'
$BOOT_TASK_PATH = '\SystemAutorun\'
$BOOT_TASK_DISPLAY = '\SystemAutorun\Boot'

# ─── 工具 ──────────────────────────────────────────────────

function Test-Admin {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Write-AutorunHost {
    param(
        [string]$Message,
        [ConsoleColor]$ForegroundColor = [ConsoleColor]::Gray
    )
    # worker / 静默模式禁止 Write-Host，避免污染 stdout（GUI NDJSON 协议）
    if ($global:AutorunQuiet) { return }
    if ($Json) { return }
    Write-Host $Message -ForegroundColor $ForegroundColor
}

function Get-AutorunProjectRoot {
    if ($global:AutorunProjectRoot -and (Test-Path -LiteralPath $global:AutorunProjectRoot)) {
        return [string]$global:AutorunProjectRoot
    }
    # scheduler.ps1 位于 <root>\lib\
    $here = $PSScriptRoot
    if ($here -and (Split-Path -Leaf $here) -eq 'lib') {
        $root = Split-Path $here -Parent
        if (Test-Path -LiteralPath (Join-Path $root 'autorun.yaml')) { return $root }
        if (Test-Path -LiteralPath (Join-Path $root 'scripts\boot.ps1')) { return $root }
    }
    if ($here -and (Test-Path -LiteralPath (Join-Path $here 'autorun.yaml'))) { return $here }
    return (Get-Location).Path
}

function Get-PwshPath {
    # 优先级：
    # 1) MSI/winget 安装的稳定路径（最适合 SYSTEM 计划任务）
    # 2) 当前进程真实路径（若不是 Local\WindowsApps 占位符）
    # 3) WindowsApps 包内真实 pwsh.exe（Store 版；SYSTEM 可能无法执行，但至少能注册）
    # 绝不使用 %LocalAppData%\Microsoft\WindowsApps\pwsh.exe 占位 stub

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($p in @(
        (Join-Path ${env:ProgramFiles} 'PowerShell\7\pwsh.exe'),
        'C:\Program Files\PowerShell\7\pwsh.exe',
        (Join-Path ${env:ProgramFiles} 'PowerShell\7-preview\pwsh.exe')
    )) {
        if ($p) { [void]$candidates.Add($p) }
    }

    try {
        $cur = (Get-Process -Id $PID).MainModule.FileName
        if ($cur) { [void]$candidates.Add($cur) }
    } catch {}

    # Store 包目录中的真实可执行文件
    $storeRoot = Join-Path ${env:ProgramFiles} 'WindowsApps'
    if (Test-Path -LiteralPath $storeRoot) {
        try {
            Get-ChildItem -LiteralPath $storeRoot -Filter 'Microsoft.PowerShell_*' -Directory -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $exe = Join-Path $_.FullName 'pwsh.exe'
                    if (Test-Path -LiteralPath $exe) { [void]$candidates.Add($exe) }
                }
        } catch {}
    }

    $cmd = Get-Command pwsh.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($cmd -and $cmd.Source) { [void]$candidates.Add($cmd.Source) }

    foreach ($p in $candidates) {
        if (-not $p) { continue }
        # 跳过 App Execution Alias 占位符
        if ($p -match '\\WindowsApps\\pwsh\.exe$') { continue }
        if ($p -match '\\Local\\Microsoft\\WindowsApps\\pwsh\.exe$') { continue }
        if (Test-Path -LiteralPath $p) {
            try { return (Resolve-Path -LiteralPath $p).Path } catch { return $p }
        }
    }

    throw "pwsh.exe not found. Please install PowerShell 7 via winget: winget install Microsoft.PowerShell"
}

function Get-BootScriptPath {
    $root = Get-AutorunProjectRoot
    $script = Join-Path $root 'scripts\boot.ps1'
    try {
        return (Resolve-Path -LiteralPath $script -ErrorAction Stop).Path
    } catch {
        throw "Boot script not found: $script"
    }
}

function Get-AutorunBootTaskViaCom {
    try {
        $svc = New-Object -ComObject 'Schedule.Service'
        $svc.Connect()
        $folder = $svc.GetFolder('\SystemAutorun')
        foreach ($flag in @(1, 0)) {
            foreach ($t in @($folder.GetTasks($flag))) {
                if ($t.Name -eq 'Boot') {
                    return [pscustomobject]@{
                        TaskName = 'Boot'
                        TaskPath = '\SystemAutorun\'
                        State    = [string]$t.State
                        Source   = 'COM'
                        ComTask  = $t
                    }
                }
            }
        }
    } catch {}
    return $null
}

function Get-AutorunBootTaskViaSchtasks {
    foreach ($tn in @('\SystemAutorun\Boot', 'SystemAutorun\Boot')) {
        try {
            $out = & schtasks.exe /Query /TN $tn /FO LIST 2>$null
            if ($LASTEXITCODE -eq 0 -and ($out -join "`n") -match 'TaskName|任务名') {
                return [pscustomobject]@{
                    TaskName = 'Boot'
                    TaskPath = '\SystemAutorun\'
                    State    = 'Ready'
                    Source   = 'schtasks'
                    Raw      = ($out -join "`n")
                }
            }
        } catch {}
    }
    return $null
}

function Get-AutorunBootTask {
    # 1) 规范 CIM 路径
    $task = Get-ScheduledTask -TaskName $BOOT_TASK_NAME -TaskPath $BOOT_TASK_PATH -ErrorAction SilentlyContinue
    if ($task) { return $task }

    # 2) 历史：TaskName 含反斜杠
    foreach ($name in @('SystemAutorun\Boot', 'SystemAutorun/Boot')) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($task) { return $task }
    }

    # 3) CIM 枚举
    $task = Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.TaskName -eq 'Boot' -and ($_.TaskPath -replace '\\+$', '') -match 'SystemAutorun$') -or
            ($_.TaskName -eq 'SystemAutorun\Boot') -or
            ($_.TaskPath -match 'SystemAutorun' -and $_.TaskName -eq 'Boot')
        } |
        Select-Object -First 1
    if ($task) { return $task }

    # 4) COM / schtasks 兜底（CIM 偶发查不到时）
    $com = Get-AutorunBootTaskViaCom
    if ($com) { return $com }
    return (Get-AutorunBootTaskViaSchtasks)
}

# ─── Boot Task CRUD ────────────────────────────────────────

function Test-BootTaskExists {
    return [bool](Get-AutorunBootTask)
}

function Register-AutorunBootTask {
    [CmdletBinding()]
    param()

    if (-not (Test-Admin)) {
        throw "Registering SYSTEM Task requires Administrator privileges. Re-run from elevated pwsh / 以管理员身份运行本工具后再安装。"
    }

    $pwsh   = Get-PwshPath
    $script = Get-BootScriptPath
    if (-not (Test-Path -LiteralPath $script)) {
        throw "Boot script not found: $script"
    }
    $usingStorePwsh = ($pwsh -match 'WindowsApps')
    if ($usingStorePwsh) {
        Write-AutorunHost "Warning: using Store pwsh for Boot Task: $pwsh (prefer winget install Microsoft.PowerShell)" -ForegroundColor Yellow
    }

    $action    = New-ScheduledTaskAction -Execute $pwsh `
                                         -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
    $trigger   = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' `
                                            -RunLevel Highest `
                                            -LogonType ServiceAccount
    $settings  = New-ScheduledTaskSettingsSet `
                    -AllowStartIfOnBatteries `
                    -DontStopIfGoingOnBatteries `
                    -RestartCount 3 `
                    -RestartInterval (New-TimeSpan -Minutes 1) `
                    -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
                    -StartWhenAvailable `
                    -MultipleInstances IgnoreNew

    $existing = Get-AutorunBootTask
    if ($existing -and $existing.PSObject.Properties['TaskName'] -and -not $existing.ComTask) {
        $samePlace = ($existing.TaskName -eq $BOOT_TASK_NAME) -and (
            (($existing.TaskPath -replace '\\+$', '\') -eq '\SystemAutorun\') -or
            $existing.TaskPath -eq $BOOT_TASK_PATH
        )
        if ($samePlace) {
            Set-ScheduledTask -TaskName $BOOT_TASK_NAME -TaskPath $BOOT_TASK_PATH `
                -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
        } else {
            try {
                Unregister-ScheduledTask -TaskName $existing.TaskName -TaskPath $existing.TaskPath -Confirm:$false -ErrorAction SilentlyContinue
            } catch {}
            Register-ScheduledTask -TaskName $BOOT_TASK_NAME -TaskPath $BOOT_TASK_PATH `
                -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        }
    } else {
        try {
            Register-ScheduledTask -TaskName $BOOT_TASK_NAME -TaskPath $BOOT_TASK_PATH `
                -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        } catch {
            # CIM 失败时回退 schtasks
            $tr = "`"$pwsh`" -NoProfile -ExecutionPolicy Bypass -File `"$script`""
            $args = @(
                '/Create',
                '/TN', $BOOT_TASK_DISPLAY,
                '/TR', $tr,
                '/SC', 'ONSTART',
                '/RU', 'SYSTEM',
                '/RL', 'HIGHEST',
                '/F'
            )
            $out = & schtasks.exe @args 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                throw "Register-ScheduledTask failed: $($_.Exception.Message); schtasks also failed: $out"
            }
        }
    }

    # 强制校验：必须能再次查到任务，否则不算安装成功
    Start-Sleep -Milliseconds 300
    $verified = Get-AutorunBootTask
    if (-not $verified) {
        # 再试 schtasks 强写一次
        $tr = "`"$pwsh`" -NoProfile -ExecutionPolicy Bypass -File `"$script`""
        $out = & schtasks.exe /Create /TN $BOOT_TASK_DISPLAY /TR $tr /SC ONSTART /RU SYSTEM /RL HIGHEST /F 2>&1 | Out-String
        Start-Sleep -Milliseconds 300
        $verified = Get-AutorunBootTask
        if (-not $verified) {
            throw "Boot Task registration reported OK but task is not queryable. schtasks output: $out"
        }
    }

    $status = Get-BootTaskStatus
    Write-AutorunHost "Installed: $BOOT_TASK_DISPLAY (state=$($status.State))" -ForegroundColor Green

    return [pscustomobject]@{
        TaskName   = $BOOT_TASK_NAME
        TaskPath   = $BOOT_TASK_PATH
        Display    = $BOOT_TASK_DISPLAY
        Executable = $pwsh
        Script     = $script
        RunAs      = 'NT AUTHORITY\SYSTEM'
        Trigger    = 'AtStartup'
        State      = $status.State
        Verified   = $true
    }
}

function Unregister-AutorunBootTask {
    [CmdletBinding()]
    param()

    if (-not (Test-Admin)) {
        throw "Unregistering Task requires Administrator privileges."
    }
    $existing = Get-AutorunBootTask
    if (-not $existing) {
        # 清理空文件夹残留
        try { & schtasks.exe /Delete /TN $BOOT_TASK_DISPLAY /F 2>$null | Out-Null } catch {}
        Write-AutorunHost "Boot Task not installed; nothing to remove." -ForegroundColor Yellow
        return
    }

    if ($existing.PSObject.Properties['ComTask'] -or $existing.Source -eq 'schtasks') {
        & schtasks.exe /Delete /TN $BOOT_TASK_DISPLAY /F 2>&1 | Out-Null
    } else {
        try {
            Unregister-ScheduledTask -TaskName $existing.TaskName -TaskPath $existing.TaskPath -Confirm:$false
        } catch {
            & schtasks.exe /Delete /TN $BOOT_TASK_DISPLAY /F 2>&1 | Out-Null
        }
    }
    Write-AutorunHost "Removed: $BOOT_TASK_DISPLAY" -ForegroundColor Green
}

function Start-BootTaskNow {
    [CmdletBinding()]
    param()
    $existing = Get-AutorunBootTask
    if (-not $existing) {
        throw "Boot Task not installed. Run 'autorun.ps1 install' first."
    }
    try {
        if ($existing.TaskPath) {
            Start-ScheduledTask -TaskName $existing.TaskName -TaskPath $existing.TaskPath -ErrorAction Stop
        } else {
            Start-ScheduledTask -TaskName $existing.TaskName -ErrorAction Stop
        }
    } catch {
        & schtasks.exe /Run /TN $BOOT_TASK_DISPLAY | Out-Null
    }
    Write-AutorunHost "Triggered: $BOOT_TASK_DISPLAY (running as SYSTEM)" -ForegroundColor Green
}

function Get-BootTaskStatus {
    $task = Get-AutorunBootTask
    if (-not $task) {
        return $null
    }

    # COM / schtasks 轻量对象
    if ($task.Source -eq 'COM' -or $task.Source -eq 'schtasks') {
        $state = if ($task.State) { [string]$task.State } else { 'Ready' }
        # COM State: 0=Unknown 1=Disabled 2=Queued 3=Ready 4=Running
        if ($state -match '^\d+$') {
            $state = switch ([int]$state) {
                1 { 'Disabled' }
                2 { 'Queued' }
                3 { 'Ready' }
                4 { 'Running' }
                default { $state }
            }
        }
        return [pscustomobject]@{
            TaskName           = 'Boot'
            TaskPath           = '\SystemAutorun\'
            Display            = $BOOT_TASK_DISPLAY
            State              = $state
            LastRunTime        = $null
            LastTaskResult     = $null
            NextRunTime        = $null
            NumberOfMissedRuns = $null
            Installed          = $true
        }
    }

    $info = $null
    try { $info = $task | Get-ScheduledTaskInfo } catch {}
    $lastRun = $null
    if ($info -and $info.LastRunTime -and $info.LastRunTime -gt [datetime]'2000-01-01') {
        $lastRun = $info.LastRunTime.ToString('yyyy-MM-dd HH:mm:ss')
    }
    $nextRun = $null
    if ($info -and $info.NextRunTime -and $info.NextRunTime -gt [datetime]'2000-01-01') {
        $nextRun = $info.NextRunTime.ToString('yyyy-MM-dd HH:mm:ss')
    }
    return [pscustomobject]@{
        TaskName           = [string]$task.TaskName
        TaskPath           = [string]$task.TaskPath
        Display            = "$($task.TaskPath)$($task.TaskName)"
        State              = [string]$task.State
        LastRunTime        = $lastRun
        LastTaskResult     = if ($info) { $info.LastTaskResult } else { $null }
        NextRunTime        = $nextRun
        NumberOfMissedRuns = if ($info) { $info.NumberOfMissedRuns } else { $null }
        Installed          = $true
    }
}

# ─── 配置同步：apply ───────────────────────────────────────
# 注意：方案 A 不为每个 yaml 任务单独创建 Task；所有任务由 boot.ps1 编排
# apply 命令实际只校验配置 + 检查 Boot Task 是否安装

function Invoke-AutorunApply {
    [CmdletBinding()]
    param($Config)

    # 已通过 Import-AutorunConfig 校验
    Write-AutorunHost "Validating autorun.yaml..." -ForegroundColor Cyan

    $enabled  = @($Config.tasks | Where-Object { $_.enabled })
    $disabled = @($Config.tasks | Where-Object { -not $_.enabled })
    $bootStatus = Get-BootTaskStatus

    # 始终返回结构化结果（worker / -Json / 人类模式共用）
    $result = [pscustomobject]@{
        enabled_tasks       = $enabled.Count
        disabled_tasks      = $disabled.Count
        boot_task           = $bootStatus
        boot_task_installed = [bool]$bootStatus
    }

    Write-AutorunHost "  Enabled tasks : $($enabled.Count)"
    Write-AutorunHost "  Disabled tasks: $($disabled.Count)"
    Write-AutorunHost ""
    Write-AutorunHost "Enabled tasks (will be started by boot.ps1 on next AtStartup):" -ForegroundColor Cyan
    $enabled | Sort-Object startup_delay | ForEach-Object {
        $dep = if ($_.depends_on) { " depends_on=[$($_.depends_on -join ', ')]" } else { '' }
        Write-AutorunHost ("  - {0,-25} {1,-12} delay={2}s{3}" -f $_.name, $_.type, $_.startup_delay, $dep)
    }
    Write-AutorunHost ""
    if ($bootStatus) {
        Write-AutorunHost "Boot Task installed: $($bootStatus.Display)" -ForegroundColor Green
        Write-AutorunHost "  State             : $($bootStatus.State)"
        Write-AutorunHost "  LastRunTime       : $($bootStatus.LastRunTime)"
        Write-AutorunHost "  LastTaskResult    : $($bootStatus.LastTaskResult)"
        Write-AutorunHost "  NextRunTime       : $($bootStatus.NextRunTime)"
    } else {
        Write-AutorunHost "Boot Task NOT installed." -ForegroundColor Yellow
        Write-AutorunHost "  Run 'autorun.ps1 install' (as Administrator) to enable AtStartup auto-launch."
        Write-AutorunHost "  Expected task: $BOOT_TASK_DISPLAY"
    }

    return $result
}
