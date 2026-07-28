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

function Assert-WindowsGuiExecutable {
    param([Parameter(Mandatory)] [string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "Published executable has an invalid DOS header: $Path"
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset + 96 -gt $bytes.Length) {
        throw "Published executable has an invalid PE offset: $Path"
    }

    if ($bytes[$peOffset] -ne 0x50 -or
        $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or
        $bytes[$peOffset + 3] -ne 0) {
        throw "Published executable has an invalid PE signature: $Path"
    }

    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $optionalHeader = $peOffset + 24
    $magic = [System.BitConverter]::ToUInt16($bytes, $optionalHeader)
    $subsystem = [System.BitConverter]::ToUInt16($bytes, $optionalHeader + 0x44)
    if ($machine -ne 0x8664 -or $magic -ne 0x20B -or $subsystem -ne 2) {
        throw "Published executable must be an x64 PE32+ Windows GUI binary: $Path"
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

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot 'publish\win-x64'
}

$outputPath = Assert-PathInsideDirectory -Path $OutputDirectory -Directory $artifactsRoot `
    -Description 'OutputDirectory'
$previousPath = Assert-PathInsideDirectory -Path "$outputPath.previous" -Directory $artifactsRoot `
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

$previousMoved = $false
$activated = $false
try {
    New-Item -ItemType Directory -Path $packagePath | Out-Null

    foreach ($project in $projects) {
        $componentPath = Join-Path $stagingPath (Join-Path 'components' $project.Name)
        New-Item -ItemType Directory -Path $componentPath | Out-Null
        $arguments = @(
            'publish',
            (Join-Path $projectRoot $project.Path),
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $componentPath,
            '-p:PublishReadyToRun=false',
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
        Merge-PublishedDirectory -Source $componentPath -Destination $project.Destination
    }

    $requiredFiles = @(
        'SystemAutorun.exe',
        'SystemAutorun.Core.dll',
        'SystemAutorun.Windows.dll',
        'SystemAutorun.runtimeconfig.json',
        'runtime\SystemAutorun.Agent.exe',
        'runtime\SystemAutorun.Elevated.exe',
        'runtime\SystemAutorun.Core.dll',
        'runtime\SystemAutorun.Windows.dll',
        'runtime\SystemAutorun.Agent.runtimeconfig.json',
        'runtime\SystemAutorun.Elevated.runtimeconfig.json'
    )
    foreach ($requiredFile in $requiredFiles) {
        $path = Join-Path $packagePath $requiredFile
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Published runtime file is missing: $path"
        }
    }

    foreach ($executable in @(
        'SystemAutorun.exe',
        'runtime\SystemAutorun.Agent.exe',
        'runtime\SystemAutorun.Elevated.exe'
    )) {
        Assert-WindowsGuiExecutable -Path (Join-Path $packagePath $executable)
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
        Move-Item -LiteralPath $outputPath -Destination $previousPath
        $previousMoved = $true
    }

    Move-Item -LiteralPath $packagePath -Destination $outputPath
    $activated = $true
}
catch {
    if ($previousMoved -and -not $activated -and -not (Test-Path -LiteralPath $outputPath)) {
        Move-Item -LiteralPath $previousPath -Destination $outputPath
        $previousMoved = $false
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
