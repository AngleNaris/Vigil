# Deployment

Updated: 2026-11-01 (Asia/Shanghai)

## Supported Environment

- Windows 10 build 19041 or later, x64
- .NET 10 SDK for development and publishing
- PowerShell 7 for the repository publish script
- Administrator approval only for boot-task and machine-wide startup changes
- Windows OpenSSH client when remote Windows/Linux overview is used

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

The [publisher](<../scripts/publish-native.ps1>) stages all four entry points,
merges only byte-identical shared files, and verifies the complete runtime
layout before activation. It checks x64 PE32+ EXE headers and each entry's
subsystem: App, Agent and Elevated use GUI (2), while CLI uses Console (3).
It writes a schema-1 SHA256 manifest without adding new manifest fields. Hashes detect changed files; they are not
a publisher signature or a trust decision. The default output is:

```text
artifacts\publish\win-x64
```

Keep the three runtime directories intact. The Console has a separate runtime
because desktop and Console runtime packs can contain different bytes under
the same filename; the publisher must reject conflicts rather than overwrite
one copy. The package layout is:

```text
win-x64/
  SystemAutorun.exe                 # GUI and its bundled desktop runtime
  publish-manifest.json
  cli/
    sautorun.exe                    # Console and its bundled non-WPF runtime
  runtime/
    SystemAutorun.Agent.exe         # GUI subsystem, no console window
    SystemAutorun.Elevated.exe
```

The CLI resolves the adjacent package `runtime` directory before development
fallbacks. Do not move the Console alone or flatten the package. GUI startup,
CLI startup and explicit shutdown still select the physical configuration root;
starting the GUI is not a prerequisite for using the CLI.

The prior complete release remains at
`artifacts\publish\win-x64.previous`. Build and validation failures leave both
release directories untouched. Activation discards the older previous
generation; if moving the new package fails after moving the current release,
the script restores the current release from `.previous`. The script does not serialize concurrent publishers, so
run only one publisher per output directory.

Run the publisher contract tests and the actual local package smoke separately:

```powershell
.\tests\PublishContract.Tests.ps1
.\tests\PublishedCli.Smoke.ps1 -PackageDirectory .\artifacts\publish\win-x64
```

The first uses synthetic, non-executable components to check headers, validation
failures and activation rollback without running MSBuild. The second checks all
manifest hashes, real entry-point headers, bundled runtime resolution and a
Console/Agent read journey in a unique empty configuration root, then stops and
cleans only that test instance. It never installs or deletes the package, changes
startup registrations, runs GUI/Elevated, or controls business workloads. This
local smoke does not replace clean-machine, GUI or privileged deployment tests.

## Install

1. Copy the complete `win-x64` directory to a protected location such as
   `C:\Program Files\SystemAutorun`.
2. Create a user-editable configuration directory containing `autorun.yaml`;
   do not place live machine configuration inside the installed binary folder.
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

User-level state stays beside the selected configuration root:

```text
autorun.yaml
autorun.overrides.json
autorun.gui.settings.json
autorun.gui.cache.json
autorun.startup.backup.json
autorun.startup.native.cache.json
logs/
backups/
```

`autorun.gui.settings.json` also contains remote-host definitions. Back it up
with the configuration root, but protect any referenced private-key and
`known_hosts` files separately.

Task and Agent category logs rotate at 5 MiB and retain three archives
(`name.log.1` through `name.log.3`). Detail-tail reads span the active file and
these archives. App startup/lifecycle fallback logs and the Elevated helper log
remain separate emergency paths.

Boot configuration is copied to:

```text
C:\ProgramData\SystemAutorun\Boot
```

App startup failures and lifecycle cleanup failures are written below the
configuration root `logs` directory when available. Elevated helper failures
are written to `%LOCALAPPDATA%\SystemAutorun\logs\elevated.log`.

## Configuration Example

Use service names, not display names, for Windows and systemd services.

```yaml
version: 1

settings:
  log_dir: logs
  backup_dir: backups
  default_startup_delay: 5

tasks:
  - name: media-indexer
    type: windows
    enabled: true
    exec: "C:\\Server\\Indexer\\Indexer.exe"
    args: ["--headless"]
    working_dir: "C:\\Server\\Indexer"
    launch:
      run_as: current_user
      trigger: logon
      window_mode: no_console
      lifecycle: tracked
    expected_running: true
    health:
      type: tcp
      host: "127.0.0.1"
      port: 8090
      timeout: 30

  - name: windows-update
    type: windows_service
    enabled: false
    service: "wuauserv"
    launch:
      run_as: current_user
      trigger: manual
      window_mode: no_console
      lifecycle: service
    health:
      type: windows_service
      service: "wuauserv"
      timeout: 30

  - name: home-api
    type: wsl_systemd
    enabled: true
    distro: "Ubuntu"
    service: "home-api.service"
    depends_on: ["media-indexer"]
    launch:
      run_as: current_user
      trigger: logon
      window_mode: no_console
      lifecycle: tracked
    health:
      type: systemd_active
      distro: "Ubuntu"
      service: "home-api.service"
      timeout: 45
```

The UI preserves the YAML file's comments, unknown fields, newline style, and
UTF-8 without BOM when adding or removing workloads.

## Remote Host Prerequisites

1. Install the Windows OpenSSH client and confirm `ssh.exe` is available.
2. Create a dedicated `known_hosts` file and populate it through an
   administrator-reviewed fingerprint exchange.
3. Configure either SSH Agent authentication or a restricted private key.
4. Grant the remote account read access to the metrics used by the fixed
   Windows or Linux probe.
5. Add the host in SystemAutorun and verify the host Tab reports `OK`.

SystemAutorun forces batch mode and strict host-key checking, does not store
passwords, and does not automatically accept a first-seen fingerprint.

## Operational Checks

After installation:

1. Run **Tools > System diagnosis** and resolve missing executables, missing
   configured services, or unwritable data directories.
2. Refresh once and confirm host CPU, memory, storage, network, and uptime are
   visible.
3. Mark every workload that must stay online as `expected_running` and confirm
   it appears on the managed-process page.
4. Review the exception-log page and confirm source, severity, and detail are
   readable.
5. Start each workload manually before enabling start-missing or automatic
   recovery.
6. Test dependency order and one expected failure path.
7. For each remote host, switch its Tab and confirm only the read-only overview
   is available.
8. Close the GUI and confirm monitoring remains available. Stop the selected
   root explicitly with `daemon stop` only when you intend to pause monitoring;
   that command does not stop managed workloads.
9. Reboot-test logon and SYSTEM boot paths separately; they have different
   privilege and session semantics.

## Rollback

1. Close the App and explicitly stop each root's Agent that uses this release
   before replacing its binaries. Closing the GUI alone does not stop monitoring.
2. Replace the installed directory with the last known-good complete release.
3. Restore the previous YAML backup if a configuration edit must be reversed.
4. Re-run boot-task installation only if the installed Agent path changed.
5. Restore the previous `autorun.yaml` or `autorun.overrides.json` only when
   the configuration change, rather than the binary release, caused the issue.

For repository-local release rollback, rename the current `win-x64` aside and
rename `win-x64.previous` to `win-x64`. Keep the directory layout intact.

## Uninstall

Disable HKCU background startup, uninstall the boot task through the elevated
action, close the GUI and explicitly stop the affected root Agents, then remove
the installed application directory.
Remove `C:\ProgramData\SystemAutorun\Boot` only after confirming its copied
configuration and logs are no longer needed.

## Known Boundaries

- This release controls one local Windows host and can read overview metrics
  from saved Windows/Linux SSH hosts. It does not expose a remote web dashboard,
  inbound network API, or remote workload-control plane.
- Docker containers are managed only indirectly through configured commands,
  services, processes, or TCP health checks.
- Share permissions, storage pools, RAID, SMART telemetry, UPS integration,
  and external notification providers are outside the current MVP.
- A portable development build intentionally cannot install the protected
  SYSTEM boot task. Install under Program Files and complete an elevated smoke
  test before relying on boot execution.
