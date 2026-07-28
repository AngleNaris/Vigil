#Requires -Version 7.0
# lib/tasks.ps1 - 任务操作（start/stop/restart），不修改 Task Scheduler
#
# 用法：在 autorun.ps1 中 dot-source
#   . (Join-Path $ProjectRoot 'lib\tasks.ps1')
# 然后：
#   Start-Task  -Task $task
#   Stop-Task   -Task $task
#   Restart-Task -Task $task

# 通用入口：根据 type 分派
function Start-Task {
    param($Task)
    $name = $Task.name
    $logMsg = "Starting ($($Task.type))..."
    Write-TaskLog -TaskName $name -Message $logMsg

    try {
        switch ($Task.type) {
            'windows'      { Start-WindowsTask -Task $Task }
            'wsl_boot'     { Start-WslBootTask -Task $Task }
            'wsl_systemd'  { Start-WslSystemdTask -Task $Task }
            'wsl_command'  { Start-WslCommandTask -Task $Task }
            default        { throw "Unknown task type: $($Task.type)" }
        }
        Write-TaskLog -TaskName $name -Message "Start succeeded."
        return $true
    } catch {
        Write-TaskLog -TaskName $name -Message "Start FAILED: $($_.Exception.Message)"
        return $false
    }
}

# 公共：确保 distro Running + 有 keepalive 保活
# wsl_systemd / wsl_command 任务在执行实际命令前必须调这个
function Ensure-WslDistroRunning {
    param($Task)

    $distro = $Task.distro
    $state = Get-WslDistroState -Distro $distro
    if (-not $state.Running) {
        Write-TaskLog -TaskName $Task.name -Message "WSL $distro not running, waking up..."
        # 复用 Start-WslBootTask 的唤醒 + keepalive 逻辑
        $bootTask = [pscustomobject]@{ name = $Task.name; type = 'wsl_boot'; distro = $distro }
        Start-WslBootTask -Task $bootTask
    } else {
        # distro 在跑，但仍需确保有 keepalive（防止 idle 后停止）
        $existing = Get-WslKeepaliveProcess -Distro $distro
        if (-not $existing) {
            Write-TaskLog -TaskName $Task.name -Message "WSL $distro running but no keepalive, spawning one..."
            Start-WslKeepalive -Task ([pscustomobject]@{ name = $Task.name; distro = $distro })
        }
    }
}

# ─── Windows 进程身份 ─────────────────────────────────────────

function ConvertTo-WindowsPathKey {
    param([AllowNull()] [string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try {
        $Path = [System.IO.Path]::GetFullPath($Path)
    } catch {}
    return $Path.TrimEnd('\').ToLowerInvariant()
}

function Initialize-WindowsProcessPathHelper {
    if ('WindowsProcessPathHelper' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WindowsProcessPathHelper {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static string GetPath(int processId) {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) { return null; }
        try {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) { return null; }
            return buffer.ToString(0, size);
        } finally {
            CloseHandle(handle);
        }
    }
}
'@
}

# 解析进程真实镜像路径。
# CIM ExecutablePath / Get-Process.Path 在部分进程上会为空（权限/完整性级别），
# 此时回退到 QueryFullProcessImageName（PROCESS_QUERY_LIMITED_INFORMATION）。
function Get-WindowsProcessImagePath {
    param(
        [Parameter(Mandatory)] [int]$ProcessId,
        [string]$CimExecutablePath,
        [string]$ProcessPath
    )

    foreach ($candidate in @($CimExecutablePath, $ProcessPath)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) { return $candidate }
    }

    try {
        Initialize-WindowsProcessPathHelper
        $apiPath = [WindowsProcessPathHelper]::GetPath($ProcessId)
        if (-not [string]::IsNullOrWhiteSpace($apiPath)) { return $apiPath }
    } catch {}

    return $null
}

function Test-PathUnderWindowsInstallTree {
    param(
        [string]$ExpectedPathKey,
        [string]$CandidatePathKey
    )
    if (-not $ExpectedPathKey -or -not $CandidatePathKey) { return $false }
    if ($ExpectedPathKey -eq $CandidatePathKey) { return $true }

    $expectedDir = [System.IO.Path]::GetDirectoryName($ExpectedPathKey)
    if ([string]::IsNullOrWhiteSpace($expectedDir)) { return $false }
    $prefix = $expectedDir.TrimEnd('\') + '\'
    return $CandidatePathKey.StartsWith($prefix, [System.StringComparison]::Ordinal)
}

function Test-WindowsProcessIdentity {
    param(
        [Parameter(Mandatory)] [string]$ExpectedPath,
        [string]$CandidatePath,
        [string]$CommandLine
    )

    $expectedKey = ConvertTo-WindowsPathKey -Path $ExpectedPath
    $candidateKey = ConvertTo-WindowsPathKey -Path $CandidatePath

    # 1) 完整路径一致
    # 2) 同安装目录树下的同名进程（调用方已按进程名过滤）
    #    例：配置入口 ...\SampleTool\SampleTool.exe，
    #    实际运行 ...\SampleTool\runtime\SampleTool.exe
    if (Test-PathUnderWindowsInstallTree -ExpectedPathKey $expectedKey -CandidatePathKey $candidateKey) {
        return $true
    }

    # 当 CIM 无法提供 ExecutablePath 时，只信任命令行的第一个 token。
    # 不能用“整条命令行包含路径”，否则路径出现在参数中时会误认同名进程。
    if ($CommandLine -and $expectedKey) {
        $commandExecutable = $null
        if ($CommandLine -match '^\s*"([^"]+)"') {
            $commandExecutable = $Matches[1]
        } elseif ($CommandLine -match '^\s*(\S+)') {
            $commandExecutable = $Matches[1]
        }
        $commandKey = ConvertTo-WindowsPathKey -Path $commandExecutable
        if (Test-PathUnderWindowsInstallTree -ExpectedPathKey $expectedKey -CandidatePathKey $commandKey) {
            return $true
        }
    }
    return $false
}

# 按 PID 批量取 CIM 详情（CommandLine / ParentProcessId / ExecutablePath）。
# 绝不全量枚举 Win32_Process：list 时每个任务扫一遍会把刷新拖到 10s+。
function Get-Win32ProcessDetailsById {
    param([int[]]$ProcessIds)

    $result = @{}
    $ids = @($ProcessIds | Where-Object { $_ -gt 0 } | Select-Object -Unique)
    if ($ids.Count -eq 0) { return $result }

    # WMI 过滤：ProcessId=1 OR ProcessId=2 ...
    # 分批避免过滤器过长（极少见，同名进程通常很少）。
    $batchSize = 32
    for ($offset = 0; $offset -lt $ids.Count; $offset += $batchSize) {
        $batch = @($ids[$offset..([math]::Min($offset + $batchSize - 1, $ids.Count - 1))])
        $filter = ($batch | ForEach-Object { "ProcessId=$_" }) -join ' OR '
        try {
            Get-CimInstance Win32_Process -Filter $filter -ErrorAction Stop | ForEach-Object {
                $result[[int]$_.ProcessId] = $_
            }
        } catch {
            # CIM 失败时退回逐个查询，仍比全量扫描轻
            foreach ($processId in $batch) {
                try {
                    $row = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction Stop |
                        Select-Object -First 1
                    if ($row) { $result[$processId] = $row }
                } catch {}
            }
        }
    }
    return $result
}

# 返回与任务 exec 完整路径匹配的进程。
# 不能只按进程名匹配：不同程序可能拥有同名的 worker.exe。
#
# 性能策略：
# 1) Get-Process -Name 按进程名过滤（O(同名实例)，毫秒级）
# 2) Path / QueryFullProcessImageName 做路径身份校验
# 3) -IncludeDetails 时才按 PID 批量查 CIM（CommandLine/Parent）
# 禁止每个任务全量扫描 Win32_Process（原先 list 可因此达到 10s+）
function Get-WindowsTaskProcesses {
    param(
        [Parameter(Mandatory)] $Task,
        [switch]$IncludeDetails
    )

    $resolvedExec = Resolve-WindowsExecutable -Executable ([string]$Task.exec)
    if (-not $resolvedExec) { return @() }

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedExec)
    if ([string]::IsNullOrWhiteSpace($baseName)) { return @() }

    $dotnetProcs = @()
    try {
        $dotnetProcs = @(Get-Process -Name $baseName -ErrorAction SilentlyContinue)
    } catch {
        return @()
    }
    if ($dotnetProcs.Count -eq 0) { return @() }

    # 健康检查/启停：默认不拉 CIM。status 详情页再 IncludeDetails。
    $cimById = @{}
    if ($IncludeDetails) {
        $cimById = Get-Win32ProcessDetailsById -ProcessIds @($dotnetProcs | ForEach-Object { [int]$_.Id })
    }

    foreach ($process in $dotnetProcs) {
        $processId = [int]$process.Id
        $cim = $cimById[$processId]
        $commandLine = if ($cim) { [string]$cim.CommandLine } else { '' }
        $imagePath = Get-WindowsProcessImagePath `
            -ProcessId $processId `
            -CimExecutablePath $(if ($cim) { [string]$cim.ExecutablePath } else { $null }) `
            -ProcessPath ([string]$process.Path)

        if (-not (Test-WindowsProcessIdentity -ExpectedPath $resolvedExec `
                -CandidatePath $imagePath `
                -CommandLine $commandLine)) { continue }

        [pscustomobject]@{
            ProcessId        = $processId
            ParentProcessId  = if ($cim -and $cim.ParentProcessId) { [int]$cim.ParentProcessId } else { 0 }
            Name             = if ($cim -and $cim.Name) { [string]$cim.Name } else { "$($process.ProcessName).exe" }
            ExecutablePath   = $(if ($imagePath) { $imagePath } elseif ($cim) { [string]$cim.ExecutablePath } else { '' })
            CommandLine      = $commandLine
            Process          = $process
        }
    }
}

function Stop-Task {
    param($Task)
    $name = $Task.name
    Write-TaskLog -TaskName $name -Message "Stopping ($($Task.type))..."

    try {
        switch ($Task.type) {
            'windows'      { Stop-WindowsTask -Task $Task }
            'wsl_boot'     { Stop-WslBootTask -Task $Task }
            'wsl_systemd'  { Stop-WslSystemdTask -Task $Task }
            'wsl_command'  { Stop-WslCommandTask -Task $Task }
            default        { throw "Unknown task type: $($Task.type)" }
        }
        Write-TaskLog -TaskName $name -Message "Stop succeeded."
        return $true
    } catch {
        Write-TaskLog -TaskName $name -Message "Stop FAILED: $($_.Exception.Message)"
        return $false
    }
}

function Restart-Task {
    param($Task)
    $stopped = Stop-Task -Task $Task
    if (-not $stopped) {
        return $false
    }
    Start-Sleep -Seconds 2
    return [bool](Start-Task -Task $Task)
}

# ─── Windows 进程 ─────────────────────────────────────────────

function Start-WindowsTask {
    param($Task)
    $executable = Resolve-WindowsExecutable -Executable $Task.exec
    if (-not $executable) {
        throw "Exec not found: $($Task.exec)"
    }

    $existing = @(Get-WindowsTaskProcesses -Task $Task)
    if ($existing.Count -gt 0) {
        $pids = ($existing | ForEach-Object { $_.ProcessId }) -join ', '
        Write-TaskLog -TaskName $Task.name -Message "Already running (managed PID=$pids); start skipped."
        return
    }

    $psi = @{
        FilePath = $executable
        PassThru = $true
    }
    if ($Task.args -and ($Task.args | Where-Object { $_ })) {
        $psi.ArgumentList = ($Task.args | Where-Object { $_ })
    }
    if ($Task.working_dir) {
        $psi.WorkingDirectory = $Task.working_dir
    }
    $proc = Start-Process @psi
    if (-not $proc) {
        throw "Failed to spawn: $executable"
    }
    Start-Sleep -Milliseconds 200
    if ($proc.HasExited) {
        throw "Process exited immediately (PID=$($proc.Id), exit=$($proc.ExitCode))"
    }
    Write-TaskLog -TaskName $Task.name -Message "Spawned PID=$($proc.Id)"
}

function Stop-WindowsTask {
    param($Task)
    $procs = @(Get-WindowsTaskProcesses -Task $Task)
    if (-not $procs) {
        Write-TaskLog -TaskName $Task.name -Message "No running managed process for '$($Task.exec)'."
        return
    }

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $procs) {
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            $deadline = (Get-Date).AddSeconds(5)
            while ((Get-Date) -lt $deadline) {
                if (-not (Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 100
            }
            if (Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue) {
                throw "process still running after 5 seconds"
            }
            Write-TaskLog -TaskName $Task.name -Message "Stopped managed PID=$($p.ProcessId)"
        } catch {
            $message = "PID=$($p.ProcessId): $($_.Exception.Message)"
            $failures.Add($message)
            Write-TaskLog -TaskName $Task.name -Message "Failed to stop $message"
        }
    }

    $remaining = @(Get-WindowsTaskProcesses -Task $Task)
    if ($failures.Count -gt 0 -or $remaining.Count -gt 0) {
        $remainingText = if ($remaining.Count -gt 0) {
            " Remaining managed PID(s): " + (($remaining | ForEach-Object { $_.ProcessId }) -join ', ') + '.'
        } else { '' }
        throw "Failed to stop '$($Task.name)'. $($failures -join '; ').$remainingText"
    }
}

function Resolve-WindowsExecutable {
    param([string]$Executable)
    if (-not $Executable) { return $null }
    if (Test-Path -LiteralPath $Executable -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Executable -ErrorAction SilentlyContinue).Path
    }
    $command = Get-Command $Executable -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($command) { return $command.Source }
    return $null
}

# ─── WSL Boot（唤醒 distro）──────────────────────────────────

function Start-WslBootTask {
    param($Task)
    # 唤醒 distro
    $null = Invoke-WslCommand -Distro $Task.distro -Command 'true' -TimeoutSec 30
    if ($LASTEXITCODE -ne 0) {
        throw "wsl.exe exited $LASTEXITCODE for distro $($Task.distro)"
    }

    # 等 distro 真正进入 Running 状态（最多 30 秒）
    $deadline = (Get-Date).AddSeconds(30)
    $running = $false
    while ((Get-Date) -lt $deadline) {
        $runningList = Get-WslRunningList
        if ($runningList -contains $Task.distro) {
            $running = $true
            $script:_WslRunningCache = $runningList
            $script:_WslRunningCacheAt = [datetime]::Now
            break
        }
        Start-Sleep -Seconds 1
    }

    if (-not $running) {
        throw "$($Task.distro) did not enter Running state within 30 seconds"
    }
    Write-TaskLog -TaskName $Task.name -Message "WSL $($Task.distro) entered Running state."

    # 启动 keepalive（防止 WSL Service 因无 wsl.exe 客户端而停止 distro）
    Start-WslKeepalive -Task $Task
}

# 启动 distro 的 keepalive 进程（hidden wsl sleep infinity）
# 原因：WSL 2 在所有 wsl.exe 客户端断开后会停止 distro（systemd service 也不能阻止）
function Start-WslKeepalive {
    param($Task)

    $existing = Get-WslKeepaliveProcess -Distro $Task.distro
    if ($existing) {
        Write-TaskLog -TaskName $Task.name -Message "Keepalive already running (PID=$($existing.Id)) for $($Task.distro)."
        return
    }

    $proc = Start-Process -FilePath 'wsl.exe' `
        -ArgumentList @('-d', $Task.distro, '--exec', '/bin/sleep', 'infinity') `
        -WindowStyle Hidden -PassThru
    if (-not $proc) { throw "Failed to start WSL keepalive for $($Task.distro)." }
    Start-Sleep -Milliseconds 200
    if ($proc.HasExited) {
        throw "WSL keepalive exited immediately for $($Task.distro) (exit=$($proc.ExitCode))."
    }
    Write-TaskLog -TaskName $Task.name -Message "Keepalive spawned PID=$($proc.Id) for $($Task.distro)."
}

# 找 keepalive 进程
function Get-WslKeepaliveProcess {
    param([string]$Distro)
    $procs = Get-CimInstance Win32_Process -Filter "Name='wsl.exe'" -ErrorAction SilentlyContinue
    $escapedDistro = [regex]::Escape([string]$Distro)
    foreach ($p in $procs) {
        if ($p.CommandLine -match "\-d\s+$escapedDistro\b" -and $p.CommandLine -match 'sleep\s+infinity') {
            $proc = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
            if ($proc) { return $proc }
        }
    }
    return $null
}

function Stop-WslBootTask {
    param($Task)
    # kill keepalive
    $keepalive = Get-WslKeepaliveProcess -Distro $Task.distro
    if ($keepalive) {
        try {
            $keepalive | Stop-Process -Force -ErrorAction Stop
            Write-TaskLog -TaskName $Task.name -Message "Keepalive PID=$($keepalive.Id) killed for $($Task.distro)."
        } catch {
            Write-TaskLog -TaskName $Task.name -Message "Failed to kill keepalive: $($_.Exception.Message)"
        }
    }
    # wsl --terminate 仅停指定 distro
    & wsl.exe --terminate $Task.distro 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "wsl --terminate exited $LASTEXITCODE"
    }
}

# ─── WSL systemd 服务 ────────────────────────────────────────

function Start-WslSystemdTask {
    param($Task)
    Ensure-WslDistroRunning -Task $Task
    $svc = $Task.service
    $out = Invoke-WslCommand -Distro $Task.distro -Command "systemctl start $svc" -User 'root' -TimeoutSec 30
    if ($LASTEXITCODE -ne 0) {
        throw "systemctl start $svc failed (exit $LASTEXITCODE): $out"
    }
}

function Stop-WslSystemdTask {
    param($Task)
    $state = Get-WslDistroState -Distro $Task.distro
    if (-not $state.Running) {
        Write-TaskLog -TaskName $Task.name -Message "WSL $($Task.distro) not running, nothing to stop."
        return
    }
    $svc = $Task.service
    $out = Invoke-WslCommand -Distro $Task.distro -Command "systemctl stop $svc" -User 'root' -TimeoutSec 30
    if ($LASTEXITCODE -ne 0) {
        throw "systemctl stop $svc failed (exit $LASTEXITCODE): $out"
    }
}

# ─── WSL 自定义命令（手动启动的进程）──────────────────────

function Start-WslCommandTask {
    param($Task)
    $user = if ($Task.user) { $Task.user } else { 'root' }
    $cmd  = $Task.command
    $work = if ($Task.working_dir) { [string]$Task.working_dir } else { '$HOME' }

    # 用 Base64 传递工作目录和命令，避免引号、空格或 shell 元字符破坏 bash -c。
    $workB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($work))
    $commandB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$cmd))
    $nameB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Task.name.ToLower()))
    $wrappedCmd = @'
set -e
WORK=$(printf '%s' '__AUTORUN_WORK_B64__' | base64 -d)
CMD=$(printf '%s' '__AUTORUN_COMMAND_B64__' | base64 -d)
NAME=$(printf '%s' '__AUTORUN_NAME_B64__' | base64 -d)
mkdir -p /tmp/autorun-logs
cd -- "$WORK"
LOG="/tmp/autorun-logs/${NAME}.log"
printf '[%s] Starting\n' "$(date '+%Y-%m-%d %H:%M:%S')" >> "$LOG"
nohup setsid bash -lc "$CMD" >> "$LOG" 2>&1 < /dev/null &
PID=$!
disown "$PID" 2>/dev/null || true
printf '[%s] Spawned PID=%s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$PID" >> "$LOG"
printf 'started PID=%s\n' "$PID"
'@
    $wrappedCmd = $wrappedCmd.Replace('__AUTORUN_WORK_B64__', $workB64)
    $wrappedCmd = $wrappedCmd.Replace('__AUTORUN_COMMAND_B64__', $commandB64)
    $wrappedCmd = $wrappedCmd.Replace('__AUTORUN_NAME_B64__', $nameB64)

    Ensure-WslDistroRunning -Task $Task

    $out = Invoke-WslCommand -Distro $Task.distro -Command $wrappedCmd -User $user -TimeoutSec 30
    if ($LASTEXITCODE -ne 0) {
        throw "WSL command launch failed (exit $LASTEXITCODE): $out"
    }
    Write-TaskLog -TaskName $Task.name -Message "WSL command output: $($out.Trim())"
}

function Stop-WslCommandTask {
    param($Task)
    # 用 health.pattern（pgrep -f 的模式）来杀进程
    $pattern = if ($Task.health.pattern) { $Task.health.pattern } else { $Task.command }
    $user = if ($Task.user) { $Task.user } else { 'root' }

    $state = Get-WslDistroState -Distro $Task.distro
    if (-not $state.Running) {
        Write-TaskLog -TaskName $Task.name -Message "WSL $($Task.distro) not running, nothing to stop."
        return
    }

    $found = Invoke-WslCommand -Distro $Task.distro `
        -Command "pgrep -f -- '$pattern' || true" `
        -User $user -TimeoutSec 15
    if ([string]::IsNullOrWhiteSpace($found.Trim())) {
        Write-TaskLog -TaskName $Task.name -Message "No matching WSL process '$pattern'."
        return
    }

    $stopCommand = @'
pkill -TERM -f -- '{0}'
rc=$?
if [ $rc -ne 0 ] && [ $rc -ne 1 ]; then exit $rc; fi
sleep 1
if pgrep -f -- '{0}' >/dev/null; then pkill -KILL -f -- '{0}' || true; fi
if pgrep -f -- '{0}' >/dev/null; then exit 1; fi
echo stopped
'@ -f $pattern
    $out = Invoke-WslCommand -Distro $Task.distro -Command $stopCommand -User $user -TimeoutSec 15
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to stop WSL process '$pattern' (exit $LASTEXITCODE): $out"
    }
    Write-TaskLog -TaskName $Task.name -Message "WSL process stop result: $($out.Trim())"
}
