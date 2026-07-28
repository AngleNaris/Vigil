# lib/health.ps1 - 健康检查（4 种）
# 用法：. $PSScriptRoot\lib\health.ps1

#Requires -Version 7.0

# 通用入口：根据 health.type 分派
# -Quick: 只试一次，不轮询（用于 list 命令快速展示状态）
function Test-TaskHealth {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)] $Task,
        [switch]$Quick
    )

    $result = [ordered]@{
        healthy = $false
        reason  = 'no-health-config'
        detail  = $null
    }

    if (-not $Task.health -or -not $Task.health.type) {
        $result.reason = 'no-health-config'
        return $result
    }

    switch ($Task.health.type) {
        'process'        { $r = Test-ProcessHealth   -Task $Task }
        'tcp'            { $r = Test-TcpHealth       -Task $Task -Quick:$Quick }
        'wsl_running'    { $r = Test-WslRunningHealth -Task $Task }
        'systemd_active' { $r = Test-SystemdHealth   -Task $Task -Quick:$Quick }
        'wsl_process'    { $r = Test-WslProcessHealth -Task $Task -Quick:$Quick }
        default          {
            $r = [ordered]@{ healthy = $false; reason = "unknown-health-type:$($Task.health.type)"; detail = $null }
        }
    }
    return $r
}

# 进程存活检查：Windows 任务优先按 exec 的完整路径匹配，避免误认同名进程。
function Test-ProcessHealth {
    param($Task)
    $pattern = $Task.health.pattern
    if (-not $pattern) {
        return [ordered]@{ healthy = $false; reason = 'missing-pattern'; detail = $null }
    }

    $processHelper = Get-Command Get-WindowsTaskProcesses -ErrorAction SilentlyContinue
    if ($Task.type -eq 'windows' -and $Task.exec -and $processHelper) {
        $proc = @(Get-WindowsTaskProcesses -Task $Task)
    } else {
        $proc = @(Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($pattern)) -ErrorAction SilentlyContinue)
    }
    if ($proc) {
        $pids = ($proc | ForEach-Object {
            if ($_.ProcessId) { $_.ProcessId } else { $_.Id }
        }) -join ', '
        return [ordered]@{
            healthy = $true
            reason  = 'process-running'
            detail  = "$($proc.Count) instance(s), PID=$pids"
        }
    }
    return [ordered]@{ healthy = $false; reason = 'process-not-found'; detail = $pattern }
}

# TCP 端口探测
function Test-TcpHealth {
    param($Task, [switch]$Quick)
    $host_ = if ($Task.health.host) { $Task.health.host } else { '127.0.0.1' }
    $port  = [int]$Task.health.port
    if (-not $port) {
        return [ordered]@{ healthy = $false; reason = 'missing-port'; detail = $null }
    }
    $timeout = if ($Task.health.timeout) { [int]$Task.health.timeout } else { 30 }

    # Quick 模式：只试一次，2s 超时
    if ($Quick) {
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $iar = $tcp.BeginConnect($host_, $port, $null, $null)
            $ok = $iar.AsyncWaitHandle.WaitOne(2000, $false)
            if ($ok -and $tcp.Connected) {
                $tcp.EndConnect($iar)
                $tcp.Close()
                return [ordered]@{ healthy = $true; reason = 'tcp-open'; detail = "${host_}:$port" }
            }
            $tcp.Close()
        } catch {}
        return [ordered]@{ healthy = $false; reason = 'tcp-closed'; detail = "${host_}:$port" }
    }

    $deadline = (Get-Date).AddSeconds($timeout)
    $lastErr = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $iar = $tcp.BeginConnect($host_, $port, $null, $null)
            $ok = $iar.AsyncWaitHandle.WaitOne(2000, $false)
            if ($ok -and $tcp.Connected) {
                $tcp.EndConnect($iar)
                $tcp.Close()
                return [ordered]@{
                    healthy = $true
                    reason  = 'tcp-open'
                    detail  = "${host_}:$port"
                }
            }
            $tcp.Close()
        }
        catch {
            $lastErr = $_.Exception.Message
        }
        Start-Sleep -Seconds 2
    }
    return [ordered]@{
        healthy = $false
        reason  = 'tcp-closed'
        detail  = "${host_}:$port (waited ${timeout}s)$(if($lastErr){' / '+$lastErr})"
    }
}

# WSL 发行版是否运行中
function Test-WslRunningHealth {
    param($Task)
    $distro = if ($Task.health.distro) { $Task.health.distro } else { $Task.distro }
    $state = Get-WslDistroState -Distro $distro
    if ($state.Running) {
        return [ordered]@{
            healthy = $true
            reason  = 'wsl-running'
            detail  = "$distro v$($state.Version)"
        }
    }
    return [ordered]@{
        healthy = $false
        reason  = 'wsl-stopped'
        detail  = $distro
    }
}

# systemd is-active 短缓存：list 刷新 / 连续操作时避免反复 spawn wsl.exe
$script:_SystemdActiveCache = @{}
$script:_SystemdActiveCacheTtlSec = 5

function Get-SystemdActiveCached {
    param(
        [Parameter(Mandatory)] [string]$Distro,
        [Parameter(Mandatory)] [string]$Service,
        [switch]$Quick
    )

    $cacheKey = "${Distro}|${Service}"
    $now = [datetime]::Now
    $cached = $script:_SystemdActiveCache[$cacheKey]
    if ($cached -and ($now - $cached.at).TotalSeconds -lt $script:_SystemdActiveCacheTtlSec) {
        return $cached.result
    }

    $activeText = Invoke-WslCommand -Distro $Distro `
        -Command "systemctl is-active --quiet '$Service' && echo OK || echo FAIL" `
        -TimeoutSec $(if ($Quick) { 8 } else { 10 })
    $result = [ordered]@{
        ok     = [bool]($activeText -match 'OK')
        detail = "${Distro}:${Service}"
    }
    $script:_SystemdActiveCache[$cacheKey] = @{ at = $now; result = $result }
    return $result
}

# systemd 服务是否 active
function Test-SystemdHealth {
    param($Task, [switch]$Quick)
    $distro  = if ($Task.health.distro)  { $Task.health.distro }  else { $Task.distro }
    $service = if ($Task.health.service) { $Task.health.service } else { $Task.service }
    if (-not $distro -or -not $service) {
        return [ordered]@{ healthy = $false; reason = 'missing-distro-or-service'; detail = $null }
    }
    $timeout = if ($Task.health.timeout) { [int]$Task.health.timeout } else { 30 }

    # 先确认 WSL 在跑（systemctl 在 WSL 关闭时无意义）
    $wslState = Get-WslDistroState -Distro $distro
    if (-not $wslState.Running) {
        return [ordered]@{
            healthy = $false
            reason  = 'wsl-not-running'
            detail  = "${distro} state=$($wslState.State)"
        }
    }

    # Quick 模式：只试一次（带短缓存）
    if ($Quick) {
        $cached = Get-SystemdActiveCached -Distro $distro -Service $service -Quick
        if ($cached.ok) {
            return [ordered]@{ healthy = $true; reason = 'systemd-active'; detail = $cached.detail }
        }
        return [ordered]@{ healthy = $false; reason = 'systemd-not-active'; detail = $cached.detail }
    }

    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        # 非 Quick 轮询时绕过缓存，保证等待启动时能看到状态变化
        $active = Invoke-WslCommand -Distro $distro -Command "systemctl is-active --quiet '$service' && echo OK || echo FAIL" -TimeoutSec 10
        if ($active -match 'OK') {
            $script:_SystemdActiveCache["${distro}|${service}"] = @{
                at     = [datetime]::Now
                result = [ordered]@{ ok = $true; detail = "${distro}:$service" }
            }
            return [ordered]@{
                healthy = $true
                reason  = 'systemd-active'
                detail  = "${distro}:$service"
            }
        }
        Start-Sleep -Seconds 3
    }
    return [ordered]@{
        healthy = $false
        reason  = 'systemd-not-active'
        detail  = "${distro}:$service (waited ${timeout}s)"
    }
}

# WSL 内进程存活检查（pgrep -f <pattern>）
function Test-WslProcessHealth {
    param($Task, [switch]$Quick)
    $distro  = if ($Task.health.distro)  { $Task.health.distro }  else { $Task.distro }
    $pattern = $Task.health.pattern
    if (-not $distro -or -not $pattern) {
        return [ordered]@{ healthy = $false; reason = 'missing-distro-or-pattern'; detail = $null }
    }

    # 先确认 WSL 在跑（避免触发自动启动，保持只读）
    $wslState = Get-WslDistroState -Distro $distro
    if (-not $wslState.Running) {
        return [ordered]@{
            healthy = $false
            reason  = 'wsl-not-running'
            detail  = "${distro} state=$($wslState.State)"
        }
    }

    $timeout = if ($Task.health.timeout) { [int]$Task.health.timeout } else { 30 }
    $deadline = (Get-Date).AddSeconds($timeout)
    $lastOut = ''
    while ((Get-Date) -lt $deadline) {
        # pgrep -f 匹配完整命令行；-a 显示完整信息便于调试
        $out = Invoke-WslCommand -Distro $distro -Command "pgrep -f '$pattern' | head -1" -TimeoutSec 10
        $lastOut = $out.Trim()
        if ($lastOut -match '^\d+$') {
            $pids = (Invoke-WslCommand -Distro $distro -Command "pgrep -f '$pattern' | wc -l" -TimeoutSec 10).Trim()
            return [ordered]@{
                healthy = $true
                reason  = 'wsl-process-running'
                detail  = "${distro}:$pattern (PID=$lastOut, count=$pids)"
            }
        }
        if ($Quick) { break }
        Start-Sleep -Seconds 3
    }
    return [ordered]@{
        healthy = $false
        reason  = 'wsl-process-not-found'
        detail  = "${distro}:$pattern"
    }
}

# 工具：读取 WSL 发行版状态
# 注：HKCU\Lxss 的 State 字段在 WSL2 6.x 不实时更新，所以用 wsl --list --running 实测
# 结果在脚本级缓存 5 秒，避免 list 多任务时反复 spawn wsl.exe
$script:_WslRunningCache = $null
$script:_WslRunningCacheAt = [datetime]::MinValue

function Get-WslRunningList {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "${env:WINDIR}\System32\wsl.exe"
    $psi.Arguments = '--list --quiet --running'
    $psi.RedirectStandardOutput = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::Unicode
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    try {
        $p = [System.Diagnostics.Process]::Start($psi)
        $outputTask = $p.StandardOutput.ReadToEndAsync()
        if (-not $p.WaitForExit(3000)) {
            try { $p.Kill($true) } catch { try { $p.Kill() } catch {} }
            try { $p.WaitForExit(1000) } catch {}
            return @()
        }
        $output = $outputTask.GetAwaiter().GetResult()
        return ($output -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    } catch {
        return @()
    }
}

function Get-WslDistroState {
    param([string]$Distro)

    if (([datetime]::Now - $script:_WslRunningCacheAt).TotalSeconds -gt 5) {
        $script:_WslRunningCache = Get-WslRunningList
        $script:_WslRunningCacheAt = [datetime]::Now
    }
    $isRunning = $script:_WslRunningCache -contains $Distro

    $lxssRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    $version = '?'
    $installed = $false
    if (Test-Path $lxssRoot) {
        $found = Get-ChildItem $lxssRoot | ForEach-Object {
            $p = Get-ItemProperty $_.PSPath
            if ($p.DistributionName -eq $Distro) { $p }
        } | Select-Object -First 1
        if ($found) {
            $installed = $true
            if ($found.Version) { $version = [int]$found.Version }
        }
    }

    if (-not $installed) {
        return [ordered]@{ Distro = $Distro; State = 'NotInstalled'; Version = '?'; Running = $false }
    }

    return [ordered]@{
        Distro  = $Distro
        State   = if ($isRunning) { 'Running' } else { 'Stopped' }
        Version = $version
        Running = $isRunning
    }
}

# 工具：执行 WSL 命令（强制不走 Git Bash 路径转换，并真正执行超时控制）
function Invoke-WslCommand {
    param(
        [Parameter(Mandatory)] [string]$Distro,
        [Parameter(Mandatory)] [string]$Command,
        [string]$User,
        [int]$TimeoutSec = 30
    )

    if ($User) {
        $wslArgs = @('-d', $Distro, '-u', $User, '--', 'bash', '-c', $Command)
    } else {
        $wslArgs = @('-d', $Distro, '--', 'bash', '-c', $Command)
    }

    $wslPath = Join-Path $env:WINDIR 'System32\wsl.exe'
    if (-not (Test-Path -LiteralPath $wslPath -PathType Leaf)) { $wslPath = 'wsl.exe' }
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $wslPath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($arg in $wslArgs) { [void]$psi.ArgumentList.Add([string]$arg) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Failed to start wsl.exe for distro '$Distro'." }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timeoutMs = [math]::Max(1, $TimeoutSec) * 1000
    if (-not $process.WaitForExit($timeoutMs)) {
        try { $process.Kill($true) } catch { try { $process.Kill() } catch {} }
        try { $process.WaitForExit(2000) } catch {}
        $global:LASTEXITCODE = 124
        throw "WSL command timed out after ${TimeoutSec}s (distro=$Distro)."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $global:LASTEXITCODE = $process.ExitCode
    if ($stderr) { return ($stdout + $stderr) }
    return $stdout
}
