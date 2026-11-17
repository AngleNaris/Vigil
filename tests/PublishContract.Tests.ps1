# Pester-free publisher contract tests. Synthetic files are NEVER executed.
# Run in a fresh pwsh process; command doubles exist only in this test process.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publisher = Join-Path $repo 'scripts\publish-native.ps1'
$artifacts = Join-Path $repo 'artifacts'
$owner = 'publish-contract-' + [guid]::NewGuid().ToString('N')
$fixture = Join-Path $artifacts ('verification\' + $owner)
$script:passed = 0

# Import only trusted repository function definitions, never publisher top-level statements.
$tokens = $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($publisher, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Publisher has PowerShell parse errors.' }
foreach ($node in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($node.Extent.Text))
}

function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $script:passed++
}
function Reject([scriptblock]$Action, [string]$Message) {
    $caught = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike ('*' + $Message + '*')) { throw }
        $caught = $true
    }
    Check $caught ('Expected rejection: ' + $Message)
}
function PeBytes([int]$Subsystem) {
    $data = [byte[]]::new(512)
    $data[0] = 0x4D; $data[1] = 0x5A
    [BitConverter]::GetBytes([int]128).CopyTo($data, 0x3C)
    [BitConverter]::GetBytes([uint32]0x4550).CopyTo($data, 128)
    [BitConverter]::GetBytes([uint16]0x8664).CopyTo($data, 132)
    [BitConverter]::GetBytes([uint16]240).CopyTo($data, 148)
    [BitConverter]::GetBytes([uint16]0x22).CopyTo($data, 150)
    [BitConverter]::GetBytes([uint16]0x20B).CopyTo($data, 152)
    [BitConverter]::GetBytes([uint16]$Subsystem).CopyTo($data, 220)
    return ,$data
}
function Write-SyntheticComponent([string]$Directory, [string]$Name) {
    $stem = switch ($Name) { App { 'Vigil' } Cli { 'vigil' } Agent { 'Vigil.Agent' } Elevated { 'Vigil.Elevated' } default { throw 'Unexpected component' } }
    $subsystem = if ($Name -eq 'Cli' -and $global:SystemAutorunPublishTestMode -ne 'wrong-pe') { 3 } else { 2 }
    [IO.File]::WriteAllBytes((Join-Path $Directory ($stem + '.exe')), (PeBytes $subsystem))
    foreach ($file in @(($stem + '.dll'), ($stem + '.deps.json'), 'SystemAutorun.Core.dll', 'SystemAutorun.Windows.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        if ($Name -eq 'App' -and $file -eq 'hostfxr.dll' -and $global:SystemAutorunPublishTestMode -eq 'missing-runtime') { continue }
        [IO.File]::WriteAllText((Join-Path $Directory $file), 'synthetic-not-loadable')
    }
    if ($Name -eq 'Elevated' -and $global:SystemAutorunPublishTestMode -eq 'hash-conflict') {
        [IO.File]::WriteAllText((Join-Path $Directory 'SystemAutorun.Core.dll'), 'conflicting-synthetic-asset')
    }
    $config = if ($Name -eq 'Cli' -and $global:SystemAutorunPublishTestMode -eq 'framework-dependent') {
        '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
    } else { '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}' }
    [IO.File]::WriteAllText((Join-Path $Directory ($stem + '.runtimeconfig.json')), $config)
    if ($Name -eq 'Cli' -and $global:SystemAutorunPublishTestMode -eq 'wpf-cli') {
        [IO.File]::WriteAllText((Join-Path $Directory 'PresentationFramework.dll'), 'synthetic-not-loadable')
    }
}

# Doubles intercept only this child process's publisher calls; they never run dotnet/MSBuild.
function dotnet {
    if ($global:SystemAutorunPublishTestMode -eq 'publish-failure') { $global:LASTEXITCODE = 17; return }
    $words = @($args)
    $index = [array]::IndexOf($words, '--output')
    if ($index -lt 0) { throw 'Expected publisher --output argument' }
    $directory = [string]$words[$index + 1]
    Write-SyntheticComponent $directory (Split-Path $directory -Leaf)
    $global:LASTEXITCODE = 0
}
function Move-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination)
    if ($global:SystemAutorunPublishTestMode -eq 'activation-failure' -and (Split-Path $LiteralPath -Leaf) -eq 'package') {
        throw 'Injected activation failure'
    }
    Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
}

try {
    $null = New-Item -ItemType Directory -Path $fixture
    $pe = Join-Path $fixture 'header.exe'
    foreach ($subsystem in @(2, 3)) {
        [IO.File]::WriteAllBytes($pe, (PeBytes $subsystem))
        Assert-WindowsExecutable $pe $subsystem
        $script:passed++
        Reject { Assert-WindowsExecutable $pe (5 - $subsystem) } 'subsystem'
    }
    foreach ($fault in @('short', 'dos', 'negative', 'overflow', 'signature', 'machine', 'optional-size', 'optional-end', 'magic', 'dll', 'not-executable')) {
        $data = PeBytes 3
        switch ($fault) {
            short { $data = [byte[]]::new(16) }
            dos { $data[0] = 0 }
            negative { [BitConverter]::GetBytes([int]-1).CopyTo($data, 0x3C) }
            overflow { [BitConverter]::GetBytes([int]::MaxValue).CopyTo($data, 0x3C) }
            signature { $data[128] = 0 }
            machine { $data[133] = 0 }
            optional-size { $data[148] = 0 }
            optional-end { [BitConverter]::GetBytes([uint16]65535).CopyTo($data, 148) }
            magic { $data[153] = 0 }
            dll { [BitConverter]::GetBytes([uint16]0x2022).CopyTo($data, 150) }
            not-executable { [BitConverter]::GetBytes([uint16]0x20).CopyTo($data, 150) }
        }
        [IO.File]::WriteAllBytes($pe, $data)
        Reject { Assert-WindowsExecutable $pe 3 } 'Published executable'
        # Rejection must close its file handle too.
        $exclusive = [IO.File]::Open($pe, 'Open', 'ReadWrite', 'None'); $exclusive.Dispose()
    }
    Reject { Assert-PathInsideDirectory $repo $artifacts 'test' } 'must be inside'

    foreach ($mode in @('publish-failure', 'wrong-pe', 'missing-runtime', 'wpf-cli', 'framework-dependent', 'hash-conflict', 'activation-failure', 'success')) {
        $global:SystemAutorunPublishTestMode = $mode
        $output = Join-Path $fixture $mode
        $null = New-Item -ItemType Directory -Path $output
        $sentinel = Join-Path $output 'owner.txt'
        [IO.File]::WriteAllText($sentinel, $owner)
        $expected = switch ($mode) {
            publish-failure { 'dotnet publish failed' }
            wrong-pe { 'subsystem' }
            missing-runtime { 'Published runtime file is missing' }
            wpf-cli { 'CLI must not include the WPF runtime' }
            framework-dependent { 'bundled self-contained runtime' }
            hash-conflict { 'Published projects produced conflicting files' }
            activation-failure { 'Injected activation failure' }
            success { $null }
        }
        if ($mode -notin @('success', 'activation-failure')) {
            $null = New-Item -ItemType Directory -Path ($output + '.previous')
            [IO.File]::WriteAllText((Join-Path ($output + '.previous') 'owner.txt'), $owner + '-older')
        }
        $before = if (Test-Path -LiteralPath (Join-Path $artifacts '.staging')) {
            @(Get-ChildItem -LiteralPath (Join-Path $artifacts '.staging') -Directory | ForEach-Object Name)
        } else { @() }
        if ($mode -eq 'success') {
            & $publisher -OutputDirectory $output -NoRestore | Out-Null
            Check (Test-Path -LiteralPath (Join-Path $output 'cli\vigil.exe')) 'CLI missing in synthetic package'
            Check ([IO.File]::ReadAllText((Join-Path ($output + '.previous') 'owner.txt')) -eq $owner) 'Previous release not retained'
            $manifest = [IO.File]::ReadAllText((Join-Path $output 'publish-manifest.json')) | ConvertFrom-Json
            Check ($manifest.schema_version -eq 1 -and $manifest.self_contained) 'Manifest shape changed unexpectedly'
            foreach ($entry in $manifest.files) {
                $file = Join-Path $output $entry.path
                Check ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -eq $entry.sha256) 'Synthetic manifest hash mismatch'
            }
        } else {
            Reject { & $publisher -OutputDirectory $output -NoRestore } $expected
            Check ([IO.File]::ReadAllText($sentinel) -eq $owner) ('Current release changed in ' + $mode)
            if ($mode -eq 'activation-failure') {
                Check (-not (Test-Path -LiteralPath ($output + '.previous'))) 'Rollback left the moved current release behind'
            } else {
                Check ([IO.File]::ReadAllText((Join-Path ($output + '.previous') 'owner.txt')) -eq ($owner + '-older')) ('Pre-activation failure changed previous in ' + $mode)
            }
        }
        $after = @(Get-ChildItem -LiteralPath (Join-Path $artifacts '.staging') -Directory | ForEach-Object Name)
        Check (@($after | Where-Object { $_ -notin $before }).Count -eq 0) ('Staging leaked in ' + $mode)
    }
    Write-Output ("Publish contract assertions passed: " + $script:passed)
}
finally {
    $global:SystemAutorunPublishTestMode = $null
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path $resolved -Leaf) -ne $owner -or $resolved -ne (Join-Path $artifacts ('verification\' + $owner))) {
        throw 'Refuse non-owned fixture cleanup'
    }
    Remove-SafeDirectoryTree -Path $resolved -TrustedRoot $artifacts
}
