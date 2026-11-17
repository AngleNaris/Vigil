[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'

function Assert-PathInsideDirectory {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [string] $Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullDirectory = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($Directory))
    $prefix = $fullDirectory + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be inside $fullDirectory"
    }

    return $fullPath
}

function Assert-NoReparsePoints {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $TrustedRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($TrustedRoot))
    if ($fullPath -ne $fullRoot) {
        $null = Assert-PathInsideDirectory -Path $fullPath -Directory $fullRoot `
            -Description 'Protected publish path'
    }

    $current = $fullPath
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            throw "Unable to resolve an existing parent for publish path: $fullPath"
        }

        $current = $parent
    }

    while ($true) {
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publish path cannot contain a reparse point: $current"
        }

        if ($current -eq $fullRoot) {
            return
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            throw "Publish path is outside its trusted root: $fullPath"
        }

        $current = $parent
    }
}

function Remove-SafeDirectoryTree {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $TrustedRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $fullPath = Assert-PathInsideDirectory -Path $Path -Directory $TrustedRoot `
        -Description 'Directory removal target'
    Assert-NoReparsePoints -Path $fullPath -TrustedRoot $TrustedRoot
    $rootItem = Get-Item -LiteralPath $fullPath -Force
    if (-not $rootItem.PSIsContainer) {
        throw "Directory removal target is not a directory: $fullPath"
    }

    foreach ($item in Get-ChildItem -LiteralPath $fullPath -Force) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-Item -LiteralPath $item.FullName -Force
        }
        elseif ($item.PSIsContainer) {
            Remove-SafeDirectoryTree -Path $item.FullName -TrustedRoot $TrustedRoot
        }
        else {
            Remove-Item -LiteralPath $item.FullName -Force
        }
    }

    Remove-Item -LiteralPath $fullPath -Force
}

function Assert-WindowsExecutable {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [ValidateSet(2, 3)] [int] $ExpectedSubsystem
    )

    # Inspect bounded headers, not the whole executable. This is not signature/trust validation.
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "Published executable has an invalid DOS header: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = [long] $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset + 24 -gt $stream.Length) {
            throw "Published executable has an invalid PE offset: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Published executable has an invalid PE signature: $Path"
        }
        $machine = $reader.ReadUInt16()
        $stream.Position = $peOffset + 20
        $optionalSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        $optionalHeader = $peOffset + 24
        if ($optionalSize -lt 0x70 -or $optionalHeader + $optionalSize -gt $stream.Length) {
            throw "Published executable has an invalid optional header: $Path"
        }
        $stream.Position = $optionalHeader
        $magic = $reader.ReadUInt16()
        $stream.Position = $optionalHeader + 0x44
        $subsystem = $reader.ReadUInt16()
        if ($machine -ne 0x8664 -or $magic -ne 0x20B -or $subsystem -ne $ExpectedSubsystem `
            -or ($characteristics -band 0x0002) -eq 0 -or ($characteristics -band 0x2000) -ne 0) {
            throw "Published executable must be x64 PE32+ EXE with subsystem $ExpectedSubsystem (GUI=2, Console=3): $Path"
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Merge-PublishedDirectory {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination
    )

    foreach ($sourceFile in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        $relativePath = [System.IO.Path]::GetRelativePath($Source, $sourceFile.FullName)
        $destinationFile = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationFile
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $destinationDirectory | Out-Null
        }

        if (Test-Path -LiteralPath $destinationFile -PathType Leaf) {
            $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash
            if ($sourceHash -ne $destinationHash) {
                throw "Published projects produced conflicting files: $relativePath"
            }

            continue
        }

        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile
    }
}

function Sync-SafeDirectoryInPlace {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $TrustedRoot
    )

    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }
    Assert-NoReparsePoints -Path $Destination -TrustedRoot $TrustedRoot

    $expectedRelative = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($sourceFile in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        $relativePath = [System.IO.Path]::GetRelativePath($Source, $sourceFile.FullName)
        $null = $expectedRelative.Add($relativePath)
        $destinationFile = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationFile
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }
        Assert-NoReparsePoints -Path $destinationDirectory -TrustedRoot $TrustedRoot
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile -Force
    }

    foreach ($existingFile in Get-ChildItem -LiteralPath $Destination -File -Recurse) {
        $relativePath = [System.IO.Path]::GetRelativePath($Destination, $existingFile.FullName)
        if (-not $expectedRelative.Contains($relativePath)) {
            Remove-Item -LiteralPath $existingFile.FullName -Force
        }
    }

    foreach ($dir in Get-ChildItem -LiteralPath $Destination -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
        if (($dir.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publish path cannot contain a reparse point: $($dir.FullName)"
        }
        if (-not (Get-ChildItem -LiteralPath $dir.FullName -Force)) {
            Remove-Item -LiteralPath $dir.FullName -Force
        }
    }
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot 'publish\win-x64'
}

$outputPath = Assert-PathInsideDirectory -Path $OutputDirectory -Directory $artifactsRoot `
    -Description 'OutputDirectory'
$previousPath = Assert-PathInsideDirectory -Path (Join-Path $artifactsRoot 'rollback\win-x64.previous') -Directory $artifactsRoot `
    -Description 'Previous release directory'
$stagingParent = Join-Path $artifactsRoot '.staging'
$stagingPath = Join-Path $stagingParent ([guid]::NewGuid().ToString('N'))
$packagePath = Join-Path $stagingPath 'package'
Assert-NoReparsePoints -Path $artifactsRoot -TrustedRoot $projectRoot
Assert-NoReparsePoints -Path $stagingPath -TrustedRoot $artifactsRoot
Assert-NoReparsePoints -Path $outputPath -TrustedRoot $artifactsRoot
Assert-NoReparsePoints -Path $previousPath -TrustedRoot $artifactsRoot

$projects = @(
    @{
        Name = 'App'
        Path = 'src\SystemAutorun.App\SystemAutorun.App.csproj'
        Destination = $packagePath
    },
    @{
        Name = 'Cli'
        Path = 'src\SystemAutorun.Cli\SystemAutorun.Cli.csproj'
        Destination = (Join-Path $packagePath 'cli')
    },
    @{
        Name = 'Agent'
        Path = 'src\SystemAutorun.Agent\SystemAutorun.Agent.csproj'
        Destination = (Join-Path $packagePath 'runtime')
    },
    @{
        Name = 'Elevated'
        Path = 'src\SystemAutorun.Elevated\SystemAutorun.Elevated.csproj'
        Destination = (Join-Path $packagePath 'runtime')
    }
)

$previousBackedUp = $false
$activated = $false
try {
    New-Item -ItemType Directory -Path $packagePath | Out-Null

    foreach ($project in $projects) {
        $componentPath = Join-Path $stagingPath (Join-Path 'components' $project.Name)
        New-Item -ItemType Directory -Path $componentPath | Out-Null
        $readyToRun = if ($project.Name -in @('App', 'Cli')) { 'true' } else { 'false' }
        $arguments = @(
            'publish',
            (Join-Path $projectRoot $project.Path),
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $componentPath,
            "-p:PublishReadyToRun=$readyToRun",
            '-p:PublishTrimmed=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false'
        )
        if ($NoRestore) {
            $arguments += '--no-restore'
        }

        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $($project.Path)"
        }

        if (-not (Test-Path -LiteralPath $project.Destination -PathType Container)) {
            New-Item -ItemType Directory -Path $project.Destination | Out-Null
        }
        if ($project.Name -eq 'Cli') {
            foreach ($wpfFile in @('PresentationFramework.dll', 'PresentationCore.dll', 'wpfgfx_cor3.dll', 'PresentationNative_cor3.dll')) {
                if (Test-Path -LiteralPath (Join-Path $componentPath $wpfFile)) {
                    throw "CLI must not include the WPF runtime: $wpfFile"
                }
            }
        }
        Merge-PublishedDirectory -Source $componentPath -Destination $project.Destination
    }

    $requiredFiles = @(
        'Vigil.exe',
        'SystemAutorun.Core.dll',
        'SystemAutorun.Windows.dll',
        'Vigil.runtimeconfig.json',
        'Vigil.dll',
        'Vigil.deps.json',
        'cli\vigil.exe',
        'cli\vigil.dll',
        'cli\vigil.deps.json',
        'cli\vigil.runtimeconfig.json',
        'cli\SystemAutorun.Core.dll',
        'cli\coreclr.dll',
        'cli\hostfxr.dll',
        'cli\hostpolicy.dll',
        'cli\System.Private.CoreLib.dll',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'System.Private.CoreLib.dll',
        'runtime\Vigil.Agent.exe',
        'runtime\Vigil.Agent.dll',
        'runtime\Vigil.Agent.deps.json',
        'runtime\Vigil.Elevated.exe',
        'runtime\Vigil.Elevated.dll',
        'runtime\Vigil.Elevated.deps.json',
        'runtime\coreclr.dll',
        'runtime\hostfxr.dll',
        'runtime\hostpolicy.dll',
        'runtime\System.Private.CoreLib.dll',
        'runtime\SystemAutorun.Core.dll',
        'runtime\SystemAutorun.Windows.dll',
        'runtime\Vigil.Agent.runtimeconfig.json',
        'runtime\Vigil.Elevated.runtimeconfig.json'
    )
    foreach ($requiredFile in $requiredFiles) {
        $path = Join-Path $packagePath $requiredFile
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Published runtime file is missing: $path"
        }
    }

    $entryPoints = @(
        @{ Path = 'Vigil.exe'; Subsystem = 2 },
        @{ Path = 'cli\vigil.exe'; Subsystem = 3 },
        @{ Path = 'runtime\Vigil.Agent.exe'; Subsystem = 2 },
        @{ Path = 'runtime\Vigil.Elevated.exe'; Subsystem = 2 }
    )
    foreach ($entry in $entryPoints) {
        Assert-WindowsExecutable -Path (Join-Path $packagePath $entry.Path) -ExpectedSubsystem $entry.Subsystem
        $configPath = [System.IO.Path]::ChangeExtension((Join-Path $packagePath $entry.Path), '.runtimeconfig.json')
        $options = ([System.IO.File]::ReadAllText($configPath) | ConvertFrom-Json).runtimeOptions
        if ($null -eq $options -or $null -ne $options.framework -or $null -ne $options.frameworks `
            -or 'Microsoft.NETCore.App' -notin @($options.includedFrameworks.name)) {
            throw "Entry point must use its bundled self-contained runtime: $($entry.Path)"
        }
    }

    $manifestFiles = foreach ($file in Get-ChildItem -LiteralPath $packagePath -File -Recurse |
        Sort-Object FullName) {
        [ordered]@{
            path = [System.IO.Path]::GetRelativePath($packagePath, $file.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            size = $file.Length
        }
    }
    $manifest = [ordered]@{
        schema_version = 1
        runtime = 'win-x64'
        self_contained = $true
        created_utc = [DateTimeOffset]::UtcNow.ToString('O')
        files = @($manifestFiles)
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText(
        (Join-Path $packagePath 'publish-manifest.json'),
        $manifestJson + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    $outputParent = Split-Path -Parent $outputPath
    if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
        New-Item -ItemType Directory -Path $outputParent | Out-Null
    }

    if (Test-Path -LiteralPath $previousPath) {
        Remove-SafeDirectoryTree -Path $previousPath -TrustedRoot $artifactsRoot
    }
    Assert-NoReparsePoints -Path $outputPath -TrustedRoot $artifactsRoot
    Assert-NoReparsePoints -Path $previousPath -TrustedRoot $artifactsRoot
    if (Test-Path -LiteralPath $outputPath) {
        Sync-SafeDirectoryInPlace -Source $outputPath -Destination $previousPath -TrustedRoot $artifactsRoot
        $previousBackedUp = $true
        # Keep artifacts\publish\win-x64 in place so Explorer/Directory Opus handles never follow a backup rename.
        Sync-SafeDirectoryInPlace -Source $packagePath -Destination $outputPath -TrustedRoot $artifactsRoot
    }
    else {
        Move-Item -LiteralPath $packagePath -Destination $outputPath
    }
    $activated = $true
}
catch {
    if ($previousBackedUp -and -not $activated -and (Test-Path -LiteralPath $previousPath)) {
        try { Sync-SafeDirectoryInPlace -Source $previousPath -Destination $outputPath -TrustedRoot $artifactsRoot }
        catch { Write-Warning "Rollback in-place sync failed: $($_.Exception.Message)" }
    }

    throw
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-SafeDirectoryTree -Path $stagingPath -TrustedRoot $artifactsRoot
    }
}

Write-Output $outputPath
Write-Output (Join-Path $outputPath 'publish-manifest.json')
