#Requires -Version 7.0
# lib/startup.ps1 - Windows Startup / Run item management and GUI self-start.

$STARTUP_BACKUP_FILE = Join-Path $PSScriptRoot '..' 'autorun.startup.backup.json'
$SELF_STARTUP_VALUE_NAME = 'SystemAutorunManager'

function Test-StartupElevated {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Test-StartupSourceRequiresAdmin {
    param([string]$Source)
    return $Source -in @('HKLM', 'HKLM-WOW64', 'Common Startup')
}

function Assert-StartupCanModify {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Action
    )
    if ((Test-StartupSourceRequiresAdmin -Source $Source) -and -not (Test-StartupElevated)) {
        throw "启动项来源 '$Source' 属于全局系统启动位置，$Action 需要管理员权限。请右键 dist\SystemAutorun.exe 以管理员身份运行后重试。"
    }
}

function Get-StartupBackupDir {
    $dir = Join-Path $PSScriptRoot '..\backups\startup'
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Get-StartupBackupEntries {
    if (-not (Test-Path $STARTUP_BACKUP_FILE)) { return @() }
    try {
        $loaded = Get-Content $STARTUP_BACKUP_FILE -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $loaded) { return @() }
        return @($loaded)
    } catch {
        return @()
    }
}

function Save-StartupBackupEntries {
    param([Parameter(Mandatory)] [array]$Entries)
    $Entries | ConvertTo-Json -Depth 10 | Set-Content $STARTUP_BACKUP_FILE -Encoding UTF8
}

function Get-RunKeyPathFromSource {
    param([Parameter(Mandatory)] [string]$Source)
    switch ($Source) {
        'HKCU'       { return 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' }
        'HKLM'       { return 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' }
        'HKLM-WOW64' { return 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' }
        default      { return $null }
    }
}

function Find-StartupEntry {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Name,
        [string]$Origin
    )

    $matches = @(Get-AutorunEntries | Where-Object {
        $_.Source -eq $Source -and $_.Name -eq $Name
    })
    if ($Origin) {
        $matches = @($matches | Where-Object { $_.Origin -eq $Origin })
    }
    if ($matches.Count -eq 0) {
        throw "Startup entry not found: source='$Source' name='$Name'"
    }
    if ($matches.Count -gt 1) {
        throw "Multiple startup entries match source='$Source' name='$Name'; pass origin to disambiguate."
    }
    return $matches[0]
}

function New-StartupBackupRecord {
    param(
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)] [string]$Action
    )
    return [pscustomobject]@{
        id          = [guid]::NewGuid().ToString('N')
        action      = $Action
        disabled_at = (Get-Date).ToString('o')
        restored_at = $null
        source      = $Entry.Source
        name        = $Entry.Name
        raw_command = $Entry.RawCommand
        target      = $Entry.Target
        args        = $Entry.Arguments
        working_dir = $Entry.WorkingDir
        origin      = $Entry.Origin
        backup_path = $null
    }
}

function Disable-StartupEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Name,
        [string]$Origin
    )

    $entry = Find-StartupEntry -Source $Source -Name $Name -Origin $Origin
    Assert-StartupCanModify -Source $entry.Source -Action '禁用'
    $record = New-StartupBackupRecord -Entry $entry -Action 'disable'

    switch ($entry.Source) {
        { $_ -in @('Startup', 'Common Startup') } {
            if (-not $entry.Origin -or -not (Test-Path $entry.Origin)) {
                throw "Startup shortcut not found: $($entry.Origin)"
            }
            $backupDir = Get-StartupBackupDir
            $backupPath = Join-Path $backupDir "$($record.id).lnk"
            Move-Item -LiteralPath $entry.Origin -Destination $backupPath -Force
            $record.backup_path = $backupPath
        }
        default {
            $runKey = Get-RunKeyPathFromSource -Source $entry.Source
            if (-not $runKey) {
                throw "Unsupported startup source: $($entry.Source)"
            }
            Remove-ItemProperty -Path $runKey -Name $entry.Name -ErrorAction Stop
        }
    }

    $backups = @(Get-StartupBackupEntries)
    $backups += $record
    Save-StartupBackupEntries -Entries $backups

    return [pscustomobject]@{
        disabled = $true
        backup   = $record
    }
}

function Restore-StartupEntry {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string]$Id)

    $backups = @(Get-StartupBackupEntries)
    $record = $backups | Where-Object { $_.id -eq $Id } | Select-Object -First 1
    if (-not $record) { throw "Startup backup not found: $Id" }
    if ($record.restored_at) { throw "Startup backup already restored: $Id" }
    Assert-StartupCanModify -Source $record.source -Action '恢复'

    switch ($record.source) {
        { $_ -in @('Startup', 'Common Startup') } {
            if ($record.backup_path -and (Test-Path $record.backup_path)) {
                $targetDir = Split-Path $record.origin -Parent
                if (-not (Test-Path $targetDir)) {
                    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
                }
                if (Test-Path $record.origin) {
                    throw "Cannot restore; target already exists: $($record.origin)"
                }
                Move-Item -LiteralPath $record.backup_path -Destination $record.origin
            } else {
                New-StartupShortcut -Path $record.origin -Target $record.target -Arguments $record.args -WorkingDir $record.working_dir
            }
        }
        default {
            $runKey = Get-RunKeyPathFromSource -Source $record.source
            if (-not $runKey) {
                throw "Unsupported startup source: $($record.source)"
            }
            if (-not (Test-Path $runKey)) {
                New-Item -Path $runKey -Force | Out-Null
            }
            New-ItemProperty -Path $runKey -Name $record.name -Value $record.raw_command -PropertyType String -Force | Out-Null
        }
    }

    foreach ($b in $backups) {
        if ($b.id -eq $Id) {
            $b.restored_at = (Get-Date).ToString('o')
            break
        }
    }
    Save-StartupBackupEntries -Entries $backups

    return [pscustomobject]@{
        restored = $true
        id       = $Id
        source   = $record.source
        name     = $record.name
    }
}

function New-StartupShortcut {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Target,
        [string]$Arguments,
        [string]$WorkingDir
    )
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath = $Target
    if ($Arguments) { $lnk.Arguments = $Arguments }
    if ($WorkingDir) { $lnk.WorkingDirectory = $WorkingDir }
    $lnk.Save()
}

function Get-DisabledStartupEntries {
    @(Get-StartupBackupEntries | Where-Object { -not $_.restored_at }) | ForEach-Object {
        [pscustomobject]@{
            id          = $_.id
            source      = $_.source
            name        = $_.name
            target      = $_.target
            args        = $_.args
            origin      = $_.origin
            disabled_at = $_.disabled_at
        }
    }
}

function Get-SelfStartupCommand {
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $exe = Join-Path $root 'dist\SystemAutorun.exe'
    if (Test-Path $exe) {
        return "`"$exe`""
    }
    $vbs = Join-Path $root 'autorun-gui.vbs'
    if (Test-Path $vbs) {
        return "wscript.exe `"$vbs`""
    }
    throw "No GUI launcher found. Expected dist\SystemAutorun.exe or autorun-gui.vbs under $root"
}

function Get-SelfStartupStatus {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $value = $null
    if (Test-Path $runKey) {
        $props = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
        if ($props) {
            $value = $props.PSObject.Properties |
                Where-Object { $_.Name -eq $SELF_STARTUP_VALUE_NAME } |
                Select-Object -ExpandProperty Value -First 1
        }
    }
    return [pscustomobject]@{
        enabled = [bool]$value
        name    = $SELF_STARTUP_VALUE_NAME
        source  = 'HKCU'
        command = $value
        desired = Get-SelfStartupCommand
    }
}

function Enable-SelfStartup {
    [CmdletBinding()]
    param()
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) {
        New-Item -Path $runKey -Force | Out-Null
    }
    $command = Get-SelfStartupCommand
    New-ItemProperty -Path $runKey -Name $SELF_STARTUP_VALUE_NAME -Value $command -PropertyType String -Force | Out-Null
    return Get-SelfStartupStatus
}

function Disable-SelfStartup {
    [CmdletBinding()]
    param()
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path $runKey) {
        Remove-ItemProperty -Path $runKey -Name $SELF_STARTUP_VALUE_NAME -ErrorAction SilentlyContinue
    }
    return Get-SelfStartupStatus
}

function ConvertTo-YamlSingleQuoted {
    param([AllowNull()] [object]$Value)
    if ($null -eq $Value) { return "''" }
    $escaped = ([string]$Value).Replace("'", "''")
    return "'$escaped'"
}

function ConvertTo-YamlInlineStringArray {
    param([array]$Values)
    $items = @($Values | Where-Object { $null -ne $_ -and [string]$_ -ne '' } | ForEach-Object {
        ConvertTo-YamlSingleQuoted $_
    })
    if ($items.Count -eq 0) { return '[]' }
    return "[$($items -join ', ')]"
}

function Remove-TaskFromConfigFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [string]$Path = (Join-Path $PSScriptRoot '..' 'autorun.yaml')
    )

    $name = $Name.Trim()
    if (-not $name) { throw 'Task name is required.' }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Config file not found: $Path"
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]](Get-Content -LiteralPath $Path -Encoding UTF8))

    # 匹配 tasks 下的 "- name: xxx" / "- name: 'xxx'"
    $namePattern = '^\s*-\s*name:\s*(?:''(?<n>[^'']+)''|"(?<n>[^"]+)"|(?<n>\S+))\s*$'
    $startIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $namePattern) {
            $found = $Matches['n']
            if ($found -eq $name) {
                $startIndex = $i
                break
            }
        }
    }
    if ($startIndex -lt 0) {
        throw "Task not found in config: $name"
    }

    # 从该任务行删到下一条 "- name:" 或顶层键（非缩进行）之前
    $endIndex = $lines.Count
    for ($j = $startIndex + 1; $j -lt $lines.Count; $j++) {
        $line = $lines[$j]
        if ($line -match $namePattern) {
            $endIndex = $j
            break
        }
        # 顶层键（无缩进且非空、非注释）视为 tasks 段结束
        if ($line -match '^[A-Za-z_][A-Za-z0-9_]*\s*:') {
            $endIndex = $j
            break
        }
    }

    $removedCount = $endIndex - $startIndex
    $lines.RemoveRange($startIndex, $removedCount)

    # 压缩任务删除后留下的多余空行（最多保留一个）
    $cleaned = [System.Collections.Generic.List[string]]::new()
    $blankRun = 0
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            $blankRun++
            if ($blankRun -le 1) { $cleaned.Add($line) }
            continue
        }
        $blankRun = 0
        $cleaned.Add($line)
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllLines($Path, $cleaned.ToArray(), $utf8NoBom)

    try { $null = Remove-TaskOverride -Name $name } catch {}

    return [pscustomobject]@{
        removed = $true
        name    = $name
        path    = $Path
    }
}

function Add-WindowsTaskToConfigFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)] [string]$PayloadJson,
        [string]$Path = (Join-Path $PSScriptRoot '..' 'autorun.yaml')
    )

    try {
        $payload = $PayloadJson | ConvertFrom-Json
    } catch {
        throw "Invalid add-windows JSON payload: $($_.Exception.Message)"
    }

    $name = ([string]$payload.name).Trim()
    $exec = ([string]$payload.exec).Trim()
    if (-not $name) { throw "Task name is required." }
    if (-not $exec) { throw "Executable path is required." }
    if (Get-TaskByName -Config $Config -Name $name) {
        throw "Task already exists: $name"
    }
    if (-not (Test-Path $exec)) {
        throw "Executable path does not exist: $exec"
    }

    $description = if ($payload.description) { [string]$payload.description } else { $name }
    $workingDir = if ($payload.working_dir) { [string]$payload.working_dir } else { Split-Path $exec -Parent }
    $delay = if ($null -ne $payload.startup_delay -and [string]$payload.startup_delay -ne '') {
        [int]$payload.startup_delay
    } elseif ($Config.settings.default_startup_delay) {
        [int]$Config.settings.default_startup_delay
    } else {
        5
    }
    $enabled = if ($null -ne $payload.enabled) { [bool]$payload.enabled } else { $true }
    $args = @()
    if ($payload.args -is [array]) {
        $args = @($payload.args)
    } elseif ($payload.args_text) {
        $args = @([string]$payload.args_text)
    }
    $healthPattern = if ($payload.health_pattern) {
        [string]$payload.health_pattern
    } else {
        [System.IO.Path]::GetFileName($exec)
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('')
    $lines.Add('  - name: ' + (ConvertTo-YamlSingleQuoted $name))
    $lines.Add('    type: windows')
    $lines.Add('    enabled: ' + ($(if ($enabled) { 'true' } else { 'false' })))
    $lines.Add('    description: ' + (ConvertTo-YamlSingleQuoted $description))
    $lines.Add('    exec: ' + (ConvertTo-YamlSingleQuoted $exec))
    if ($args.Count -gt 0) {
        $lines.Add('    args: ' + (ConvertTo-YamlInlineStringArray $args))
    }
    if ($workingDir) {
        $lines.Add('    working_dir: ' + (ConvertTo-YamlSingleQuoted $workingDir))
    }
    $lines.Add('    startup_delay: ' + $delay)
    $lines.Add('    health:')
    $lines.Add('      type: process')
    $lines.Add('      pattern: ' + (ConvertTo-YamlSingleQuoted $healthPattern))

    Add-Content -Path $Path -Value ($lines -join [Environment]::NewLine) -Encoding UTF8

    return [pscustomobject]@{
        added         = $true
        name          = $name
        exec          = $exec
        enabled       = $enabled
        startup_delay = $delay
    }
}
