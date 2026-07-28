# lib/config.ps1 - 配置加载与校验
# 用法：在 autorun.ps1 中 dot-source：. $PSScriptRoot\lib\config.ps1

#Requires -Version 7.0

$AUTORUN_OVERRIDE_FILE = Join-Path $PSScriptRoot '..' 'autorun.overrides.json'

function Import-AutorunConfig {
    [CmdletBinding()]
    param(
        [string]$Path = (Join-Path $PSScriptRoot '..' 'autorun.yaml')
    )

    if (-not (Test-Path $Path)) {
        throw "Config file not found: $Path"
    }

    try {
        Import-Module powershell-yaml -ErrorAction Stop
    }
    catch {
        throw "powershell-yaml module not available. Install with: Install-PSResource powershell-yaml"
    }

    $raw = Get-Content -Raw -Path $Path
    $config = $null
    try {
        $config = ConvertFrom-Yaml -Yaml $raw
    }
    catch {
        throw "Failed to parse YAML: $($_.Exception.Message)"
    }

    if (-not $config) { throw "Empty config: $Path" }
    if ($config.version -ne 1) { throw "Unsupported config version: $($config.version)" }
    if (-not $config.tasks) { throw "Config has no 'tasks' section" }
    if (-not $config.settings) { throw "Config has no 'settings' section" }

    Expand-ConfigPaths -Config $config
    Test-ConfigSchema -Config $config
    Apply-TaskOverrides -Config $config

    return $config
}

# 应用 override（不修改 yaml 文件，保留注释和格式）
function Apply-TaskOverrides {
    param($Config)

    if (-not (Test-Path $AUTORUN_OVERRIDE_FILE)) { return }

    try {
        $overrides = Get-Content $AUTORUN_OVERRIDE_FILE -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        return
    }
    if (-not $overrides) { return }

    foreach ($t in $Config.tasks) {
        $match = $overrides | Where-Object { $_.name -eq $t.name } | Select-Object -First 1
        if ($match -and $null -ne $match.enabled) {
            $t.enabled = [bool]$match.enabled
        }
    }
}

# 设置/清除 override
function Set-TaskOverride {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [bool]$Enabled
    )

    $overrides = @()
    if (Test-Path $AUTORUN_OVERRIDE_FILE) {
        try {
            $loaded = Get-Content $AUTORUN_OVERRIDE_FILE -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($loaded) { $overrides = @($loaded) }
        } catch {}
    }

    $found = $false
    foreach ($o in $overrides) {
        if ($o.name -eq $Name) {
            $o.enabled = $Enabled
            $found = $true
            break
        }
    }
    if (-not $found) {
        $overrides += [pscustomobject]@{ name = $Name; enabled = $Enabled }
    }

    $overrides | ConvertTo-Json -Depth 5 | Set-Content $AUTORUN_OVERRIDE_FILE -Encoding UTF8
}

# 清除单项 override（让 yaml 默认值生效）
function Remove-TaskOverride {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string]$Name)

    if (-not (Test-Path $AUTORUN_OVERRIDE_FILE)) { return $false }

    try {
        $overrides = @(Get-Content $AUTORUN_OVERRIDE_FILE -Raw -Encoding UTF8 | ConvertFrom-Json)
    } catch { return $false }
    if (-not $overrides) { return $false }

    $filtered = @($overrides | Where-Object { $_.name -ne $Name })
    if ($filtered.Count -eq 0) {
        Remove-Item $AUTORUN_OVERRIDE_FILE -Force
    } else {
        $filtered | ConvertTo-Json -Depth 5 | Set-Content $AUTORUN_OVERRIDE_FILE -Encoding UTF8
    }
    return $true
}

# 把 settings.log_dir 等相对路径转为绝对路径（基于项目根目录）
function Expand-ConfigPaths {
    param($Config)
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    if ($Config.settings.log_dir -and -not [System.IO.Path]::IsPathRooted($Config.settings.log_dir)) {
        $Config.settings.log_dir = (Join-Path $root $Config.settings.log_dir)
    }
    if ($Config.settings.backup_dir -and -not [System.IO.Path]::IsPathRooted($Config.settings.backup_dir)) {
        $Config.settings.backup_dir = (Join-Path $root $Config.settings.backup_dir)
    }
}

# schema 校验：每个 task 必须有 name/type/enabled，按 type 校验专属字段
function Test-ConfigSchema {
    param($Config)

    $seen = @{}
    foreach ($t in $config.tasks) {
        if (-not $t.name) { throw "Task missing 'name': $($t | ConvertTo-Json -Compress)" }
        if ($seen.ContainsKey($t.name)) { throw "Duplicate task name: $($t.name)" }
        $seen[$t.name] = $true

        if (-not $t.type) { throw "Task '$($t.name)' missing 'type'" }
        $validTypes = 'windows', 'wsl_boot', 'wsl_systemd', 'wsl_command'
        if ($validTypes -notcontains $t.type) {
            throw "Task '$($t.name)' has invalid type '$($t.type)'. Valid: $($validTypes -join ', ')"
        }

        switch ($t.type) {
            'windows' {
                if (-not $t.exec) { throw "Task '$($t.name)' (windows) missing 'exec'" }
            }
            'wsl_boot' {
                if (-not $t.distro) { throw "Task '$($t.name)' (wsl_boot) missing 'distro'" }
            }
            'wsl_systemd' {
                if (-not $t.distro)  { throw "Task '$($t.name)' (wsl_systemd) missing 'distro'" }
                if (-not $t.service) { throw "Task '$($t.name)' (wsl_systemd) missing 'service'" }
            }
            'wsl_command' {
                if (-not $t.distro)  { throw "Task '$($t.name)' (wsl_command) missing 'distro'" }
                if (-not $t.command) { throw "Task '$($t.name)' (wsl_command) missing 'command'" }
            }
        }

        if ($t.depends_on) {
            $depList = @($t.depends_on)
            if ($depList.Count -eq 0 -or ($depList | Where-Object { $_ -isnot [string] }).Count -gt 0) {
                throw "Task '$($t.name)' depends_on must be a string array"
            }
        }
    }

    # 校验 depends_on 引用存在
    foreach ($t in $config.tasks) {
        if ($t.depends_on) {
            foreach ($dep in @($t.depends_on)) {
                if (-not $seen.ContainsKey($dep)) {
                    throw "Task '$($t.name)' depends on unknown task '$dep'"
                }
            }
        }
    }
}

function Get-TaskByName {
    param([Parameter(Mandatory)] $Config, [Parameter(Mandatory)] [string]$Name)
    return $Config.tasks | Where-Object { $_.name -eq $Name } | Select-Object -First 1
}
