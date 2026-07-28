# Deployment

## Supported Environment

- Windows 10 build 19041 or later, x64
- .NET 10 SDK for development and publishing
- PowerShell 7 for the repository publish script
- Administrator approval only for boot-task and machine-wide startup changes

The release is self-contained; target machines do not need a separately
installed .NET runtime.

## Build And Test

```powershell
dotnet test SystemAutorun.sln --configuration Debug
dotnet test SystemAutorun.sln --configuration Release
```

## Publish

```powershell
.\scripts\publish-native.ps1 -NoRestore
```

The script publishes each entry point to a unique staging directory, merges
only identical shared files, verifies required runtime files and x64 Windows
GUI PE metadata, writes SHA256 `publish-manifest.json`, then switches the
release directory. The default output is:

```text
artifacts\publish\win-x64
```

The prior complete release remains at
`artifacts\publish\win-x64.previous`. A failed publish before activation leaves
the current release untouched; a failed activation restores `.previous`.

## Install

1. Copy the complete `win-x64` directory to a protected location such as
   `C:\Program Files\SystemAutorun`.
2. Keep the user-editable `autorun.yaml` in a separate configuration directory.
3. Launch `SystemAutorun.exe --root <configuration-directory>`.
4. Enable "run in background after user logon" in the app when HKCU startup is
   required.
5. Use the boot-task action and approve UAC only when SYSTEM boot tasks are
   required.

Do not flatten the `runtime` directory. The App resolves Agent and Elevated
from that directory, while the elevated helper requires Agent beside itself.

## Background Launch

For a manual background start:

```powershell
& 'C:\Program Files\SystemAutorun\SystemAutorun.exe' `
  --background `
  --root 'C:\path\to\configuration'
```

The App and Agent allocate no console and the main WPF window remains hidden.
Configured third-party GUI tasks may need their own silent or tray argument to
guarantee that they never display a window.

## Data And Logs

User-level state stays beside the selected configuration root and is excluded
from source control. Boot configuration is copied to:

```text
C:\ProgramData\SystemAutorun\Boot
```

App startup failures and lifecycle cleanup failures are written below the
configuration root `logs` directory when available. Elevated helper failures
are written to `%LOCALAPPDATA%\SystemAutorun\logs\elevated.log`.

## Rollback

1. Exit the App from its tray menu.
2. Replace the installed directory with the last known-good complete release.
3. Restore the previous YAML backup if a configuration edit must be reversed.
4. Re-run boot-task installation only if the installed Agent path changed.

For repository-local release rollback, rename the current `win-x64` aside and
rename `win-x64.previous` to `win-x64`. Keep the directory layout intact.

## Uninstall

Disable HKCU background startup, uninstall the boot task through the elevated
action, exit the tray process, then remove the installed application directory.
Remove `C:\ProgramData\SystemAutorun\Boot` only after confirming its copied
configuration and logs are no longer needed.
