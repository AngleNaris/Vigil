#Requires -Version 7.0
# autorun-worker.ps1 - 常驻 CLI worker（方案 A）
#
# GUI 通过 stdin/stdout 以 NDJSON 交互，避免每次刷新冷启动 pwsh。
# 任务列表始终从 autorun.yaml（+ overrides）动态加载，不硬编码。
#
# 协议（一行一个 JSON）：
#   → {"id":"1","command":"list","args":[]}
#   ← {"id":"1","ok":true,"data":[...]}
#   ← {"id":"1","ok":false,"error":"..."}
# 启动就绪：
#   ← {"event":"ready"}

$ErrorActionPreference = 'Stop'
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8

$ProjectRoot = $PSScriptRoot
$global:AutorunProjectRoot = $ProjectRoot
# 静默 + JSON 语义：禁止业务库 Write-Host 污染 NDJSON 协议
$global:AutorunQuiet = $true
$Json = $true
. (Join-Path $ProjectRoot 'lib\config.ps1')
. (Join-Path $ProjectRoot 'lib\health.ps1')
. (Join-Path $ProjectRoot 'lib\logger.ps1')
. (Join-Path $ProjectRoot 'lib\scan.ps1')
. (Join-Path $ProjectRoot 'lib\tasks.ps1')
. (Join-Path $ProjectRoot 'lib\scheduler.ps1')
. (Join-Path $ProjectRoot 'lib\startup.ps1')
. (Join-Path $ProjectRoot 'lib\json-command.ps1')

$script:CachedConfig = $null
$script:YamlMtimeUtc = $null
$script:OverrideMtimeUtc = $null

function Get-ConfigFileMtimes {
    $yamlPath = Join-Path $ProjectRoot 'autorun.yaml'
    $ovrPath  = Join-Path $ProjectRoot 'autorun.overrides.json'
    $yamlMtime = if (Test-Path -LiteralPath $yamlPath) {
        (Get-Item -LiteralPath $yamlPath).LastWriteTimeUtc
    } else {
        [datetime]::MinValue
    }
    $ovrMtime = if (Test-Path -LiteralPath $ovrPath) {
        (Get-Item -LiteralPath $ovrPath).LastWriteTimeUtc
    } else {
        [datetime]::MinValue
    }
    return [pscustomobject]@{ Yaml = $yamlMtime; Override = $ovrMtime }
}

function Get-WorkerConfig {
    param([switch]$Force)

    $mtimes = Get-ConfigFileMtimes
    $needReload = $Force -or
        (-not $script:CachedConfig) -or
        ($mtimes.Yaml -ne $script:YamlMtimeUtc) -or
        ($mtimes.Override -ne $script:OverrideMtimeUtc)

    if ($needReload) {
        $script:CachedConfig = Import-AutorunConfig
        Initialize-Logger -LogDir $script:CachedConfig.settings.log_dir
        $script:YamlMtimeUtc = $mtimes.Yaml
        $script:OverrideMtimeUtc = $mtimes.Override
    }
    return $script:CachedConfig
}

function Write-WorkerLine {
    param($Object)
    $json = $Object | ConvertTo-Json -Depth 14 -Compress
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

# 会改写 yaml/overrides 的命令：强制下次重载配置
$script:MutatingCommands = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('enable', 'disable', 'add-windows', 'remove-task', 'apply')
)

Write-WorkerLine ([ordered]@{ event = 'ready'; pid = $PID; root = $ProjectRoot })

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    $reqId = $null
    try {
        $req = $line | ConvertFrom-Json -ErrorAction Stop
        $reqId = $req.id
        $command = [string]$req.command
        $argsList = @()
        if ($null -ne $req.args) {
            $argsList = @($req.args | ForEach-Object { [string]$_ })
        }

        switch ($command) {
            'ping' {
                Write-WorkerLine ([ordered]@{ id = $reqId; ok = $true; data = @{ pong = $true } })
                continue
            }
            'shutdown' {
                Write-WorkerLine ([ordered]@{ id = $reqId; ok = $true; data = @{ bye = $true } })
                break
            }
            'reload-config' {
                $null = Get-WorkerConfig -Force
                Write-WorkerLine ([ordered]@{
                    id   = $reqId
                    ok   = $true
                    data = @{
                        reloaded = $true
                        tasks    = @($script:CachedConfig.tasks).Count
                    }
                })
                continue
            }
        }

        $config = Get-WorkerConfig
        $data = Invoke-AutorunJsonCommand -Command $command -Rest $argsList -Config $config
        if ($script:MutatingCommands.Contains($command)) {
            $script:CachedConfig = $null
        }
        Write-WorkerLine ([ordered]@{ id = $reqId; ok = $true; data = $data })
    } catch {
        Write-WorkerLine ([ordered]@{
            id    = $reqId
            ok    = $false
            error = $_.Exception.Message
        })
    }
}
