# Verification

Date: 2026-07-28 (Asia/Shanghai)

## Automated Tests

| Configuration | Core | Integration | Result |
| --- | ---: | ---: | --- |
| Debug | 10 | 29 | 39 passed, 0 failed |
| Release | 10 | 29 | 39 passed, 0 failed |

Commands:

```powershell
dotnet test SystemAutorun.sln --configuration Debug --no-restore
dotnet test SystemAutorun.sln --configuration Release --no-restore
```

Both runs completed with zero build warnings and zero errors. Coverage includes
configuration validation/edit preservation, overrides, IPC serialization,
native process launch and quoting, process identity, startup management,
Task Scheduler state mapping, protected-path boundary checks, allowlisted
staging cleanup, single-instance listener recovery, empty task configuration,
and first-task insertion into an empty YAML sequence.

## UI And Default Configuration

- Rebuilt the WPF shell as a dense industrial operations console with a status
  summary, command toolbar, four-column task grid, contextual inspector, stable
  global startup policy, explicit empty state, and bottom status bar.
- Preserved the existing design tokens: dark neutral surfaces, restrained
  purple interaction accents, green/red/gray status colors, zero-radius
  geometry, 1px borders, Microsoft YaHei UI, and `OK / FAIL / OFF` labels.
- Integrated `WPF-UI 4.3.0`. Dark framework resources load first, the
  product-owned token bridge overrides framework colors, and all six windows
  now derive from `FluentWindow` with no backdrop and no DWM corner rounding.
- Reduced general button height from 38 to 32 px, toolbar buttons to 30 px, and
  tightened the main header, command bar, and policy footer bands.
- The shipped `autorun.yaml` now contains `tasks: []`; repository source search
  found no remaining bundled process names.
- WPF XAML compiled in both Debug and Release with zero warnings and zero
  errors. Visual/UI automation was intentionally not used.

## Published Artifact

- Output: `artifacts\publish\win-x64`
- Previous release: `artifacts\publish\win-x64.previous`
- Runtime: self-contained `win-x64`
- Manifest entries: 680 files, all SHA256 hashes and sizes re-read successfully
- Total output: 681 files including the manifest, 318,683,345 bytes
- PDB files: none
- PE validation: App, Agent, and Elevated are x64 PE32+ Windows GUI subsystem
- Staging cleanup: no entries remained under `artifacts\.staging`

## Non-UI Background Smoke Test

UI automation was intentionally not used. The final run used an isolated root
with `tasks: []`; it did not start any configured user task.

- Published App started with `--background --root
  C:\_MY_WORK\systemautorun\.codex\smoke-release-wpfui-20260728`.
- App PID `3712` and owned Agent PID `28688` remained alive for validation.
- App and Agent `MainWindowHandle` values were both `0`.
- Agent named-pipe `ping` returned `ready` from PID `28688`; `list` returned
  zero configured tasks.
- A second background App PID `35700` exited with code `0`.
- Exactly one published App instance remained.
- SystemAutorun itself created no `cmd.exe`, PowerShell, or visible console
  window.

Cleanup sent `shutdown` to the exact Agent through its pipe, then terminated the
exact background App PID. Its three generated files and empty directories were
removed explicitly. A final check found no tracked process or isolated runtime
root. No UI automation was used.

## Deliberately Not Performed

- No UI automation, screenshots, clicks, or visual assertions.
- No real Task Scheduler registration, update, run, or uninstall.
- No ProgramData boot-config deployment from this portable development path.
- No `git push`.

The boot security path is covered by code review, path-boundary tests, Release
build, and the rule that a portable/dev Elevated helper rejects installation
because it is not under Program Files. A final installed-environment UAC and
Task Scheduler smoke test remains required before production rollout.
