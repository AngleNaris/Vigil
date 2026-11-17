# Real local package acceptance, not a clean-machine or full product acceptance.
# Reads the specified package; never deletes/installs it or changes startup registrations.
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$cli = Join-Path $package 'cli\vigil.exe'
$agentPath = Join-Path $package 'runtime\Vigil.Agent.exe'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokens = $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts\publish-native.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Publisher parse errors.' }
$node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-WindowsExecutable' }, $false)
if ($node.Count -ne 1) { throw 'Expected exactly one publisher PE validator.' }
. ([scriptblock]::Create($node[0].Extent.Text))

# Check the complete manifest, including size, relative-path uniqueness and real PE headers.
if (@(Get-ChildItem -LiteralPath $package -Force -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count) {
    throw 'Package contains a reparse point.'
}
$manifest = [IO.File]::ReadAllText((Join-Path $package 'publish-manifest.json')) | ConvertFrom-Json
if ($manifest.schema_version -ne 1 -or -not $manifest.self_contained -or $manifest.runtime -ne 'win-x64') { throw 'Invalid package manifest.' }
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $parts = ([string]$entry.path).Replace('\', '/').Split('/')
    if ([string]::IsNullOrWhiteSpace($entry.path) -or [IO.Path]::IsPathRooted($entry.path) `
        -or @($parts | Where-Object { $_ -in @('', '.', '..') }).Count -or -not $paths.Add($entry.path)) {
        throw 'Unsafe or duplicate manifest path.'
    }
    $file = Join-Path $package $entry.path
    if ((Get-Item -LiteralPath $file).Length -ne $entry.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw 'Package manifest hash/size mismatch.'
    }
}
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object Name -ne 'publish-manifest.json')
if ($files.Count -ne $paths.Count) { throw 'Manifest does not cover all package files.' }
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($package, $file.FullName).Replace('\', '/')
    if (-not $paths.Contains($relative)) { throw 'Unlisted file in package.' }
}
foreach ($entry in @(@('Vigil.exe', 2), @('cli\vigil.exe', 3), @('runtime\Vigil.Agent.exe', 2), @('runtime\Vigil.Elevated.exe', 2))) {
    Assert-WindowsExecutable (Join-Path $package $entry[0]) $entry[1]
}
Write-Output ("Manifest and four PE entries verified; files=" + $paths.Count)

$owner = 'SystemAutorun-published-' + [guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) $owner
$null = New-Item -ItemType Directory -Path $root
$yaml = "version: 1`nsettings:`n  default_startup_delay: 0`ntasks: []`n"
[IO.File]::WriteAllText((Join-Path $root 'autorun.yaml'), $yaml, [Text.UTF8Encoding]::new($false))
$agent = $null
$identity = $null
$trace = Join-Path $root 'console-host.trace'
$startedAfter = [DateTime]::UtcNow
function Invoke-Console([string[]]$Words, [bool]$EnableHostTrace = $false) {
    $info = [Diagnostics.ProcessStartInfo]::new($cli)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WorkingDirectory = $root # No source-checkout CWD fallback.
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    [void]$info.Environment.Remove('VIGIL_AGENT_PATH')
    [void]$info.Environment.Remove('VIGIL_ROOT')
    $info.Environment['DOTNET_ROOT'] = Join-Path $root 'no-global-runtime'
    $info.Environment['DOTNET_ROOT_X64'] = $info.Environment['DOTNET_ROOT']
    $info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $info.Environment['DOTNET_HOST_TRACE'] = if ($EnableHostTrace) { '1' } else { '0' }
    $info.Environment['DOTNET_HOST_TRACEFILE'] = $trace
    # Set both documented naming generations for host compatibility.
    $info.Environment['COREHOST_TRACE'] = if ($EnableHostTrace) { '1' } else { '0' }
    $info.Environment['COREHOST_TRACEFILE'] = $trace
    foreach ($word in @('--root', $root) + $Words + @('--json')) { $info.ArgumentList.Add($word) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(25000)) {
            if ($process.MainModule.FileName -ne $cli) { throw 'Refuse non-owned CLI cleanup.' }
            $process.Kill($true); $process.WaitForExit(); throw 'Published CLI timed out.'
        }
        $text = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
        if ($errorText.Length -ne 0 -or $text.Trim().Split("`n").Count -ne 1) { throw 'CLI did not preserve one-line JSON stdout / empty stderr.' }
        $body = $text | ConvertFrom-Json -Depth 50
        if ($body.schemaVersion -ne 1) { throw 'Invalid CLI schema.' }
        return [pscustomobject]@{ Exit = $process.ExitCode; Body = $body }
    } finally { $process.Dispose() }
}
function Confirm-OwnedAgent {
    if ($null -eq $agent -or $agent.HasExited -or $agent.MainModule.FileName -ne $agentPath -or $agent.StartTime.ToUniversalTime() -ne $identity) {
        throw 'Agent identity no longer belongs to this test; do not send stop/replay.'
    }
}
try {
    $version = Invoke-Console @('--version') $true
    if ($version.Exit -ne 0 -or -not $version.Body.ok) { throw 'Packaged Console failed to start.' }
    $hostTrace = [IO.File]::ReadAllText($trace)
    if (-not $hostTrace.Contains((Join-Path $package 'cli\coreclr.dll'), [StringComparison]::OrdinalIgnoreCase) `
        -or $hostTrace.Contains('PresentationFramework.dll', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Console did not resolve only its bundled non-WPF runtime.'
    }
    $offline = Invoke-Console @('daemon', 'status', '--timeout', '0.2')
    if ($offline.Exit -ne 6) { throw 'Fresh root is not offline; do not adopt an existing Agent.' }
    $start = Invoke-Console @('daemon', 'start')
    if ($start.Exit -ne 0 -or -not $start.Body.ok) { throw 'Daemon start not confirmed; inspect, do not replay.' }
    $agent = Get-Process -Id $start.Body.data.pid
    $identity = $agent.StartTime.ToUniversalTime()
    Confirm-OwnedAgent
    if ($identity -lt $startedAfter.AddSeconds(-1) -or $agent.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Unexpected Agent lifetime/window.' }
    $clr = @($agent.Modules | Where-Object ModuleName -eq 'coreclr.dll')
    if ($clr.Count -ne 1 -or $clr[0].FileName -ne (Join-Path $package 'runtime\coreclr.dll')) { throw 'Agent used an unexpected runtime.' }
    foreach ($words in @(@('daemon', 'status'), @('task', 'list'), @('config', 'show'), @('monitor', 'status'), @('logs', 'sources'), @('logs', '--tail', '5'))) {
        Confirm-OwnedAgent
        $result = Invoke-Console $words
        if ($result.Exit -ne 0 -or -not $result.Body.ok) { throw ('Packaged read failed: ' + ($words -join ' ')) }
    }
    $status = Invoke-Console @('daemon', 'status')
    if ($status.Body.data.pid -ne $agent.Id -or $status.Body.data.instanceId -ne $start.Body.data.instanceId) { throw 'Root daemon instance changed; do not stop.' }
    if ([IO.File]::ReadAllText((Join-Path $root 'autorun.yaml')) -ne $yaml) { throw 'Read journey changed fixture YAML.' }
    Confirm-OwnedAgent
    $stop = Invoke-Console @('daemon', 'stop')
    if ($stop.Exit -ne 0 -or -not $agent.WaitForExit(15000)) { throw 'Normal Agent shutdown not verified.' }
    $offlineAgain = Invoke-Console @('daemon', 'status', '--timeout', '0.2')
    if ($offlineAgain.Exit -ne 6) { throw 'Offline query restarted a daemon.' }
    Write-Output ("Published Console journey passed; Agent PID=" + $agent.Id + '; bundled runtimes; six reads; normal stop; offline exit=6.')
}
finally {
    # Never touch other roots or stop a process based only on its name/PID.
    foreach ($item in @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $agentPath -and $_.CommandLine -like ('*' + $root + '*') })) {
        $owned = Get-Process -Id $item.ProcessId
        try {
            if ($owned.MainModule.FileName -ne $agentPath -or $owned.StartTime.ToUniversalTime() -lt $startedAfter.AddSeconds(-1)) { throw 'Refuse non-owned Agent cleanup.' }
            $owned.Kill($true); $owned.WaitForExit()
            Write-Output 'Fallback cleanup used; not normal-stop evidence.'
        } finally { $owned.Dispose() }
    }
    if ($agent) { $agent.Dispose() }
    $resolved = [IO.Path]::GetFullPath($root)
    if ((Split-Path $resolved -Leaf) -ne $owner -or (Split-Path $resolved -Parent) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())) {
        throw 'Refuse non-owned smoke root cleanup.'
    }
    Remove-Item -LiteralPath $resolved -Force -Recurse
}
