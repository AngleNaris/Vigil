# Verification

Date: 2026-07-28 (Asia/Shanghai)

## Automated Tests

| Configuration | Core | Integration | Result |
| --- | ---: | ---: | --- |
| Debug | 8 | 29 | 37 passed, 0 failed |
| Release | 8 | 29 | 37 passed, 0 failed |

Commands:

```powershell
dotnet test SystemAutorun.sln --configuration Debug --no-restore
dotnet test SystemAutorun.sln --configuration Release --no-restore
```

Both runs completed with zero build warnings and zero errors. Coverage includes
configuration validation/edit preservation, overrides, IPC serialization,
native process launch and quoting, process identity, startup management,
Task Scheduler state mapping, protected-path boundary checks, allowlisted
staging cleanup, and single-instance listener recovery.

## Published Artifact

- Output: `artifacts\publish\win-x64`
- Previous release: `artifacts\publish\win-x64.previous`
- Runtime: self-contained `win-x64`
- Manifest entries: 678 files, all SHA256 hashes re-read successfully
- Total output: 679 files including the manifest, 312,090,396 bytes
- PDB files: none
- PE validation: App, Agent, and Elevated are x64 PE32+ Windows GUI subsystem
- Staging cleanup: no entries remained under `artifacts\.staging`

## Non-UI Background Smoke Test

UI automation was intentionally not used. The final run used an isolated root
with `tasks: []`; it did not start any configured user task.

- Published App started with `--background --root
  C:\_MY_WORK\systemautorun\.codex\runtime-smoke-final`.
- App PID `40064` and owned Agent PID `18724` remained alive for validation.
- App and Agent `MainWindowHandle` values were both `0`.
- Agent named-pipe `ping` returned `ready` from PID `18724`; `list` returned
  zero configured tasks.
- A second background App PID `33700` exited with code `0`.
- Exactly one published App instance remained.
- App and Agent owned no TCP or UDP endpoint.
- SystemAutorun itself created no `cmd.exe`, PowerShell, or visible console
  window.

Cleanup sent `shutdown` to the exact Agent through its pipe, then terminated the
exact path-verified background App PID. The isolated root was deleted only
after absolute-path and reparse-point validation. A final check found no tracked
or published process, TCP/UDP listener, or isolated runtime root.

## Deliberately Not Performed

- No UI automation, screenshots, clicks, or visual assertions.
- No real Task Scheduler registration, update, run, or uninstall.
- No ProgramData boot-config deployment from this portable development path.
- No `git push`.

The boot security path is covered by code review, path-boundary tests, Release
build, and the rule that a portable/dev Elevated helper rejects installation
because it is not under Program Files. A final installed-environment UAC and
Task Scheduler smoke test remains required before production rollout.
