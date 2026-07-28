# lib/scan.ps1 - 扫描系统所有自启动项
# 用法：. $PSScriptRoot\lib\scan.ps1

#Requires -Version 7.0

# 扫描所有自启动项来源，返回统一结构的数组
function Get-AutorunEntries {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.List[pscustomobject]])]
    param()

    $entries = [System.Collections.Generic.List[pscustomobject]]::new()

    # === Startup 文件夹（当前用户） ===
    $userStartup = [Environment]::GetFolderPath('Startup')
    Resolve-ShortcutFolder -Path $userStartup -Location 'Startup' -Entries $entries

    # === Common Startup 文件夹（所有用户） ===
    $commonStartup = [Environment]::GetFolderPath('CommonStartup')
    Resolve-ShortcutFolder -Path $commonStartup -Location 'Common Startup' -Entries $entries

    # === HKCU Run ===
    Resolve-RunKey -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Location 'HKCU' -Entries $entries

    # === HKLM Run (64-bit) ===
    Resolve-RunKey -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Location 'HKLM' -Entries $entries

    # === HKLM Run (32-bit WOW6432Node) ===
    Resolve-RunKey -Path 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' -Location 'HKLM-WOW64' -Entries $entries

    return $entries
}

# 解析 .lnk 文件夹
function Resolve-ShortcutFolder {
    param([string]$Path, [string]$Location, $Entries)
    if (-not (Test-Path $Path)) { return }
    $shell = New-Object -ComObject WScript.Shell
    Get-ChildItem $Path -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $lnk = $shell.CreateShortcut($_.FullName)
            $Entries.Add([pscustomobject]@{
                Name       = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
                Source     = $Location
                RawCommand = $lnk.TargetPath
                Target     = $lnk.TargetPath
                Arguments  = $lnk.Arguments
                WorkingDir = $lnk.WorkingDirectory
                Origin     = $_.FullName
            })
        }
        catch {
            $Entries.Add([pscustomobject]@{
                Name       = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
                Source     = $Location
                RawCommand = $null
                Target     = $null
                Arguments  = $null
                WorkingDir = $null
                Origin     = $_.FullName
                Error      = $_.Exception.Message
            })
        }
    }
}

# 解析注册表 Run 键
function Resolve-RunKey {
    param([string]$Path, [string]$Location, $Entries)
    if (-not (Test-Path $Path)) { return }
    $item = Get-ItemProperty -Path $Path
    $item.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object {
        $parsed = Split-CommandString -Command $_.Value
        $Entries.Add([pscustomobject]@{
            Name       = $_.Name
            Source     = $Location
            RawCommand = $_.Value
            Target     = $parsed.Exec
            Arguments  = $parsed.Args
            WorkingDir = $null
            Origin     = "$Path :: $($_.Name)"
        })
    }
}

# 把 "C:\path with space\foo.exe" --bar "x y" 拆成 (exec, args)
# 处理带引号、不带引号、以及注册表 Run 键常见的"路径含空格但无引号"情况
function Split-CommandString {
    param([string]$Command)
    if (-not $Command) { return @{ Exec = $null; Args = '' } }
    $trimmed = $Command.Trim()

    # 情况 1：开头是引号 → 按引号结束位置拆分
    if ($trimmed.StartsWith('"')) {
        $end = $trimmed.IndexOf('"', 1)
        if ($end -gt 0) {
            return @{
                Exec = $trimmed.Substring(1, $end - 1)
                Args = $trimmed.Substring($end + 1).Trim()
            }
        }
    }

    # 情况 2：未加引号但路径带空格。按扩展名边界从右向左尝试，
    # 避免参数中也包含 .exe 时过早截断，同时兼容 Run 键中的历史脏数据。
    $extensionMatches = [regex]::Matches(
        $trimmed,
        '\.(?:exe|com|bat|cmd|ps1|vbs)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    for ($i = $extensionMatches.Count - 1; $i -ge 0; $i--) {
        $end = $extensionMatches[$i].Index + $extensionMatches[$i].Length
        $candidate = $trimmed.Substring(0, $end).Trim()
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return @{
                Exec = $candidate
                Args = $trimmed.Substring($end).Trim()
            }
        }
    }

    # 情况 3：尝试逐步拼接 token 直到文件存在（应对路径含空格但无引号）
    $tokens = $trimmed -split '\s+'
    $exec = ''
    for ($i = 0; $i -lt $tokens.Count; $i++) {
        $candidate = ($tokens[0..$i] -join ' ')
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $exec = $candidate
            $args = if ($i -lt $tokens.Count - 1) { ($tokens[($i + 1)..($tokens.Count - 1)] -join ' ') } else { '' }
            return @{ Exec = $exec; Args = $args }
        }
    }

    # 情况 4：路径当前不存在时，仍保留直到可执行扩展名的完整文本。
    # 这能让 GUI 展示/编辑真实命令，而不是误导性地截成 "C:\Program"。
    if ($extensionMatches.Count -gt 0) {
        $end = $extensionMatches[0].Index + $extensionMatches[0].Length
        return @{
            Exec = $trimmed.Substring(0, $end).Trim()
            Args = $trimmed.Substring($end).Trim()
        }
    }

    # 情况 5：fallback - 第一个 token 当 exec
    return @{
        Exec = $tokens[0]
        Args = if ($tokens.Count -gt 1) { ($tokens[1..($tokens.Count - 1)] -join ' ') } else { '' }
    }
}

# 把扫描结果与 YAML 配置匹配，标注每项是否在管理范围内
function Compare-EntriesWithConfig {
    param(
        [Parameter(Mandatory)] $Entries,
        [Parameter(Mandatory)] $Config
    )

    $managedTargets = @{}
    foreach ($t in $Config.tasks) {
        if ($t.type -eq 'windows' -and $t.exec) {
            $key = (Resolve-PathSafe $t.exec).ToLower()
            $managedTargets[$key] = $t.name
        }
    }

    $result = [System.Collections.Generic.List[pscustomobject]]::new()
    foreach ($e in $Entries) {
        $matched = $null
        if ($e.Target) {
            $key = (Resolve-PathSafe $e.Target).ToLower()
            if ($managedTargets.ContainsKey($key)) {
                $matched = $managedTargets[$key]
            }
        }
        $result.Add([pscustomobject]@{
            Entry   = $e
            Managed = [bool]$matched
            Task    = $matched
        })
    }
    return $result
}

function Resolve-PathSafe {
    param([string]$Path)
    if (-not $Path) { return $Path }
    if (Test-Path $Path) {
        return (Resolve-Path $Path -ErrorAction SilentlyContinue).Path
    }
    return $Path
}
