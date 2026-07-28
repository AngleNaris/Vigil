#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent

. (Join-Path $ProjectRoot 'lib\scan.ps1')
. (Join-Path $ProjectRoot 'lib\logger.ps1')
. (Join-Path $ProjectRoot 'lib\health.ps1')
. (Join-Path $ProjectRoot 'lib\tasks.ps1')

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    if ($Actual -ne $Expected) {
        throw "$Message`nExpected: $Expected`nActual: $Actual"
    }
}

function Assert-True {
    param([bool]$Value, [string]$Message)
    if (-not $Value) { throw $Message }
}

$parsed = Split-CommandString -Command 'C:\Program Files\LM Studio\LM Studio.exe --run-as-service'
Assert-Equal $parsed.Exec 'C:\Program Files\LM Studio\LM Studio.exe' 'Unquoted executable path was truncated.'
Assert-Equal $parsed.Args '--run-as-service' 'Arguments were parsed incorrectly.'

$resolved = Resolve-WindowsExecutable -Executable 'powershell.exe'
Assert-True ([bool]$resolved) 'PATH-resolvable executables must be accepted.'

Assert-True (Test-WindowsProcessIdentity `
    -ExpectedPath 'C:\Tools\xray.exe' `
    -CandidatePath 'C:\Tools\XRAY.EXE') `
    'Windows process identity must match the same executable path case-insensitively.'
Assert-True (Test-WindowsProcessIdentity `
    -ExpectedPath 'C:\Tools\xray.exe' `
    -CommandLine '"C:\Tools\xray.exe" -config config.json') `
    'Windows process identity must match a command line containing the executable path.'
Assert-True (-not (Test-WindowsProcessIdentity `
    -ExpectedPath 'C:\Tools\xray.exe' `
    -CommandLine '"C:\Other\xray.exe" -config C:\Tools\xray.exe')) `
    'Windows process identity must not match a target path mentioned only in arguments.'
Assert-True (-not (Test-WindowsProcessIdentity `
    -ExpectedPath 'C:\Tools\xray.exe' `
    -CandidatePath 'C:\Other\xray.exe')) `
    'Same-name executables from another directory must not match.'
Assert-True (Test-WindowsProcessIdentity `
    -ExpectedPath 'C:\Program Files\Docker\Docker\Docker Desktop.exe' `
    -CandidatePath 'C:\Program Files\Docker\Docker\frontend\Docker Desktop.exe') `
    'Windows process identity must match same-name re-exec under the install directory tree.'
Assert-True (-not (Test-WindowsProcessIdentity `
    -ExpectedPath 'C:\Program Files\Docker\Docker\Docker Desktop.exe' `
    -CandidatePath 'C:\Other\Docker Desktop.exe')) `
    'Install-tree matching must not accept same-name executables outside the install directory.'

function Stop-Task { return $true }
function Start-Task { return $true }
$restart = Restart-Task -Task ([pscustomobject]@{ name = 'test' })
Assert-True ($restart -is [bool]) 'Restart-Task must return a scalar Boolean.'
Assert-True $restart 'Restart-Task should return true when stop and start succeed.'

$applyRaw = & pwsh -NoProfile -NonInteractive -File (Join-Path $ProjectRoot 'autorun.ps1') apply -Json
$apply = $applyRaw | ConvertFrom-Json
Assert-True ([bool]$apply.applied) 'apply -Json must produce a valid JSON result.'
if ($apply.boot_task) {
    Assert-True ($apply.boot_task.State -is [string]) 'apply -Json must serialize Boot Task state as a string.'
}

$statusRaw = & pwsh -NoProfile -NonInteractive -File (Join-Path $ProjectRoot 'autorun.ps1') status xray-windows -Json
$status = $statusRaw | ConvertFrom-Json
Assert-True ($status.processes -is [array]) 'status -Json must keep processes as an array when one process is present.'

$doctorRaw = & pwsh -NoProfile -NonInteractive -File (Join-Path $ProjectRoot 'autorun.ps1') doctor -Json
$doctor = $doctorRaw | ConvertFrom-Json
$schedulerCheck = @($doctor.checks | Where-Object { $_.check -eq 'Task Scheduler service' }) | Select-Object -First 1
Assert-True ($schedulerCheck -and ($schedulerCheck.detail -is [string])) 'doctor -Json must serialize Task Scheduler status as a string.'

Write-Host 'Regression checks passed.' -ForegroundColor Green
