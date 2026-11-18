[CmdletBinding()]
param(
    [string] $PublishDirectory,
    [string] $OutputDirectory,
    [switch] $FrameworkDependent,
    [switch] $SkipAppPublish,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$selfContained = -not $FrameworkDependent

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $publishLeaf = if ($selfContained) { 'publish\win-x64' } else { 'publish\win-x64-fd' }
    $PublishDirectory = Join-Path $artifactsRoot $publishLeaf
}
else {
    $PublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot 'installer'
}
else {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}

if (-not $SkipAppPublish) {
    $publishScript = Join-Path $PSScriptRoot 'publish-native.ps1'
    $pubArgs = @{ OutputDirectory = $PublishDirectory }
    if ($FrameworkDependent) {
        $pubArgs['FrameworkDependent'] = $true
    }
    if ($NoRestore) {
        $pubArgs['NoRestore'] = $true
    }
    & $publishScript @pubArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'publish-native.ps1 failed.'
    }
}

$manifestPath = Join-Path $PublishDirectory 'publish-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Published package manifest not found: $manifestPath"
}

$versionedName = if ($selfContained) { 'Vigil-Setup-1.0.0-win-x64.exe' } else { 'Vigil-Setup-1.0.0-win-x64-fd.exe' }
$latestName = if ($selfContained) { 'Vigil-Setup.exe' } else { 'Vigil-Setup-fd.exe' }

$stagingDir = Join-Path $artifactsRoot ('.installer-staging\' + [guid]::NewGuid().ToString('N'))
$installerBuildDir = Join-Path $stagingDir 'bin'
$payloadZip = Join-Path $stagingDir 'payload.zip'
$assembledExe = Join-Path $stagingDir $versionedName

try {
    New-Item -ItemType Directory -Path $installerBuildDir -Force | Out-Null

    $installerProj = Join-Path $projectRoot 'src\SystemAutorun.Installer\SystemAutorun.Installer.csproj'
    $selfContainedArg = if ($selfContained) { 'true' } else { 'false' }
    $compressionArg = if ($selfContained) { 'true' } else { 'false' }
    $dotnetArgs = @(
        'publish',
        $installerProj,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', $selfContainedArg,
        '--output', $installerBuildDir,
        "-p:SelfContained=$selfContainedArg",
        '-p:PublishSingleFile=true',
        "-p:IncludeNativeLibrariesForSelfExtract=$compressionArg",
        "-p:EnableCompressionInSingleFile=$compressionArg",
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    if ($NoRestore) {
        $dotnetArgs += '--no-restore'
    }

    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $installerProj"
    }

    $baseExe = Join-Path $installerBuildDir 'Vigil-Setup.exe'
    if (-not (Test-Path -LiteralPath $baseExe -PathType Leaf)) {
        throw "Expected single-file installer executable was not produced: $baseExe"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PublishDirectory,
        $payloadZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $baseLen = (Get-Item -LiteralPath $baseExe).Length
    $zipLen = (Get-Item -LiteralPath $payloadZip).Length
    $magic = [System.Text.Encoding]::ASCII.GetBytes('VIGILPK1')

    $outStream = [System.IO.File]::Create($assembledExe)
    try {
        $exeStream = [System.IO.File]::OpenRead($baseExe)
        try {
            $exeStream.CopyTo($outStream)
        }
        finally {
            $exeStream.Dispose()
        }

        $zipStream = [System.IO.File]::OpenRead($payloadZip)
        try {
            $zipStream.CopyTo($outStream)
        }
        finally {
            $zipStream.Dispose()
        }

        $outStream.Write([BitConverter]::GetBytes([int64]$baseLen), 0, 8)
        $outStream.Write([BitConverter]::GetBytes([int64]$zipLen), 0, 8)
        $outStream.Write($magic, 0, $magic.Length)
        $outStream.Flush()
    }
    finally {
        $outStream.Dispose()
    }

    $totalLen = (Get-Item -LiteralPath $assembledExe).Length
    if ($totalLen -ne ($baseLen + $zipLen + 24)) {
        throw "Installer size mismatch: expected $($baseLen + $zipLen + 24), actual $totalLen"
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $versionedTarget = Join-Path $OutputDirectory $versionedName
    $latestTarget = Join-Path $OutputDirectory $latestName
    Copy-Item -LiteralPath $assembledExe -Destination $versionedTarget -Force
    Copy-Item -LiteralPath $assembledExe -Destination $latestTarget -Force

    $sizeMb = [math]::Round($totalLen / 1MB, 2)
    Write-Output "Installer created ($sizeMb MB):"
    Write-Output $versionedTarget
    Write-Output $latestTarget
}
finally {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
