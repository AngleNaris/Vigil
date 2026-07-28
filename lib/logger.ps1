# lib/logger.ps1 - 日志写入 + 大小轮转
# 用法：. $PSScriptRoot\lib\logger.ps1

#Requires -Version 7.0

$script:LoggerConfig = $null

function Initialize-Logger {
    param([string]$LogDir)
    if (-not (Test-Path $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }
    $script:LoggerConfig = [pscustomobject]@{
        LogDir     = $LogDir
        MaxSizeMB  = 10
        KeepCount  = 5
    }
}

# 写一行到指定 task 的日志文件
function Write-TaskLog {
    param(
        [Parameter(Mandatory)] [string]$TaskName,
        [Parameter(Mandatory)] [string]$Message,
        [ValidateSet('INFO','WARN','ERROR','DEBUG')] [string]$Level = 'INFO'
    )
    if (-not $script:LoggerConfig) {
        Initialize-Logger -LogDir (Join-Path $PSScriptRoot '..' 'logs')
    }
    $file = Join-Path $script:LoggerConfig.LogDir "$TaskName.log"
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "[$ts] [$Level] $Message"
    Add-Content -Path $file -Value $line -Encoding UTF8

    Invoke-LogRotation -File $file
}

# 直接把外部命令的 stdout/stderr 流写到日志
function Write-TaskLogStream {
    param(
        [Parameter(Mandatory)] [string]$TaskName,
        [Parameter(Mandatory)] [string]$Text
    )
    if (-not $script:LoggerConfig) {
        Initialize-Logger -LogDir (Join-Path $PSScriptRoot '..' 'logs')
    }
    $file = Join-Path $script:LoggerConfig.LogDir "$TaskName.log"
    Add-Content -Path $file -Value $Text -Encoding UTF8
    Invoke-LogRotation -File $file
}

function Get-TaskLogPath {
    param([Parameter(Mandatory)] [string]$TaskName)
    if (-not $script:LoggerConfig) {
        Initialize-Logger -LogDir (Join-Path $PSScriptRoot '..' 'logs')
    }
    return (Join-Path $script:LoggerConfig.LogDir "$TaskName.log")
}

function Read-TaskLog {
    param(
        [Parameter(Mandatory)] [string]$TaskName,
        [int]$Tail = 50
    )
    $file = Get-TaskLogPath -TaskName $TaskName
    if (-not (Test-Path $file)) { return @() }
    return (Get-Content -Path $file -Tail $Tail -Encoding UTF8)
}

# 按大小轮转：超过 MaxSizeMB → rename 为 .log.1，旧 .log.N 顺移，超出 KeepCount 删除
function Invoke-LogRotation {
    param([string]$File)
    if (-not (Test-Path $File)) { return }
    $size = (Get-Item $File).Length
    $limit = $script:LoggerConfig.MaxSizeMB * 1MB
    if ($size -lt $limit) { return }

    $keep = $script:LoggerConfig.KeepCount
    for ($i = $keep; $i -ge 1; $i--) {
        $src = if ($i -eq 1) { $File } else { "$File.$($i - 1)" }
        $dst = "$File.$i"
        if ($i -eq $keep -and (Test-Path $dst)) {
            Remove-Item $dst -Force
        }
        if (Test-Path $src) {
            Move-Item -Path $src -Destination $dst -Force
        }
    }
}
