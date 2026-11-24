using System.Text;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Cli;

internal static class CliHelp
{
    internal static string? ResolveTopic(IReadOnlyList<string> words)
    {
        if (words.Count == 0) { return null; }
        var topic = string.Join(' ', words);
        if (topic.Equals("skill", StringComparison.OrdinalIgnoreCase)) { return "skill"; }
        var exact = CliArguments.Routes.FirstOrDefault(route => route.Key.Equals(topic, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) { return exact.Key; }
        var child = CliArguments.Routes.FirstOrDefault(route => route.Key.StartsWith(topic + " ", StringComparison.OrdinalIgnoreCase));
        if (child is not null) { return string.Join(' ', child.Key.Split(' ').Take(words.Count)); }
        // A normal invocation may include its operand before --help. Never interpret it as input or a revision.
        var parent = string.Join(' ', words.Take(words.Count - 1));
        var named = CliArguments.Routes.FirstOrDefault(route => route.Key.Equals(parent, StringComparison.OrdinalIgnoreCase)
            && (route.NeedsName || route.Key is "monitor history" or "host status"));
        if (named is not null) { return named.Key; }
        throw new ArgumentException("Unknown help topic. Run vigil --help for implemented commands.");
    }

    internal static string Render(string? topic)
    {
        if (topic is null) { return CliApplication.HelpText; }
        if (topic == "skill") { return SkillText; }
        var route = CliArguments.Routes.FirstOrDefault(item => item.Key == topic);
        var text = new StringBuilder().AppendLine($"vigil {topic}");
        if (route is not null)
        {
            text.AppendLine("Usage: vigil [--root DIR] " + Usage(route));
            text.AppendLine(Details(route.Key));
            if (route.Revision)
            {
                var source = route.Key.StartsWith("startup ", StringComparison.Ordinal) ? "startup list"
                    : route.Key == "recovery resume" ? "recovery state" : "config show";
                text.AppendLine($"Required: --expected-revision SHA256 from a fresh {source} --json response (revision).");
            }
            if (route.Confirmation) { text.AppendLine("Required: explicit --yes for this invocation; there is no interactive confirmation prompt."); }
            if (route.Rpc is not null && AgentProtocol.IsMutation(route.Rpc))
            {
                text.AppendLine("Optional: --operation-id UUIDv7. Retain the admitted ID; after an uncertain result query operation show ID, never blindly resubmit with a new ID.");
            }
            if (route.PayloadType is not null || route.Key == "monitor history")
            {
                text.AppendLine("Input: --file PATH or --file - (UTF-8 stdin). YAML for config validate/apply, JSON otherwise.");
                text.AppendLine("Input waiting is bounded by --timeout; no RPC is sent until input completes. Timeout/cancel does not prove server cancellation after dispatch.");
            }
            if (Example(route.Key) is { } example) { text.AppendLine("Input example (JSON):").AppendLine(example); }
            if (SchemaSupplement(route.Key) is { } schema) { text.AppendLine(schema); }
        }
        var children = CliArguments.Routes.Where(item => item.Key.StartsWith(topic + " ", StringComparison.Ordinal)).ToArray();
        if (children.Length != 0)
        {
            text.AppendLine("Implemented commands:");
            foreach (var child in children) { text.AppendLine("  " + Usage(child)); }
        }
        if (topic == "startup" || topic.StartsWith("startup ", StringComparison.Ordinal))
        {
            text.AppendLine("Not implemented: startup disable/restore/rebuild and startup boot install/uninstall. Self changes use prepare + set, not enable/disable aliases.");
        }
        if (topic == "config")
        {
            text.AppendLine("Configuration YAML schema & AI workflow: run vigil help skill.");
        }
        text.AppendLine("Global: --root DIR --json --timeout SECONDS (>0..600, default 30) --non-interactive --help --version");
        text.AppendLine("Help is local: no root resolution, stdin/file reads, Agent connection, daemon start or UAC. Quote names with spaces; -- ends option parsing.");
        text.AppendLine("Exit: 0 success; 1 execution/partial failure; 2 input; 3 connection/protocol; 4 permission/confirmation; 5 conflict; 6 timeout/cancel/unknown. Ctrl+C ends watch/follow with 0.");
        text.AppendLine("Use vigil help COMMAND, vigil help skill (AI skill & config schema), or vigil --help.");
        return text.ToString().TrimEnd();
    }

    private static string Usage(CliRoute route)
    {
        var operand = route.Key switch
        {
            "startup self prepare" => " enable|disable",
            "operation show" or "startup operation inspect" => " UUIDv7",
            "startup inspect" or "startup adopt" => " ENTRY_ID",
            "host show" or "host remove" => " ID",
            "autorestart run" => " RULE_ID",
            "host status" => " [ID]",
            "monitor history" => " (NAME [--window 24h|7d|30d] [--buckets 1..1000] | --file QUERY_JSON)",
            _ => route.NeedsName ? " NAME" : "",
        };
        var input = route.PayloadType is null ? "" : route.PayloadType == typeof(ConfigurationDocumentRequest) ? " --file YAML" : " --file JSON";
        return route.Key + operand + input + (route.Revision ? " --expected-revision SHA256" : "")
            + (route.Confirmation ? " --yes" : "");
    }

    private static string Details(string key) => key switch
    {
        "task add" => "Adds configuration, not an explicit launch. Review executable, arguments, health and launch settings; existing automatic policies may subsequently run tasks. The example path is a placeholder, not a runnable fixture.",
        "task list" or "task show" => "Reads configured tasks/current Agent status. Use --json for complete fields; an unavailable observation is not health.",
        "task start" or "task stop" or "task restart" or "task start-missing" => "Explicit workload action through the already-running Agent. Inspect per-task results; a partial failure is not success for every task.",
        "task remove" => "Removes this root's task configuration. Does not remove independent native/other-root startup entries.",
        "task enable" or "task disable" => "Changes the configured enabled flag using the current workspace revision.",
        "task track" or "task untrack" => "Changes expected-running tracking, not an immediate start/stop. Recovery still depends on policy and eligibility.",
        "config path" => "Resolves the configuration/root path locally; does not require an Agent.",
        "config show" => "Optional: --raw. Without --json, raw emits verbatim YAML to stdout and revision to stderr. --raw --json returns a byte-preserving base64url snapshot.",
        "config validate" or "config apply" => "Input is the complete YAML document (maximum 40 KiB), not a JSON wrapper or patch. Start from config show --raw. validate reports known-field differences without committing; apply replaces the document, preserving submitted bytes/comments/unknowns, not omitted old content. Separate overrides/preferences remain in effect.",
        "settings get" => "Returns the workspace snapshot. The complete preference object is data.preferences; revision is in the outer JSON envelope.",
        "settings set" => "Replaces the complete preferences object using snake_case keys. Copy data.preferences from settings get --json and preserve remote_hosts/log_sources/unknown settings. Do not use an empty template to edit one flag; prefer settings patch.",
        "settings patch" => "Changes only supplied policy flags using camelCase keys: autoRecoverFailedTasks and autoStartMissingOnLaunch. Unspecified flags, hosts and log sources are preserved.",
        "host list" or "host show" => "Reads this root's configured remote definitions, not a network discovery scan.",
        "host add" or "host update" or "host upsert" => "Input is a complete remote definition with an explicit stable id. add rejects existing IDs; update rejects absent IDs and replaces the full definition; upsert creates/replaces. The disabled example uses a documentation address; configure your own known_hosts file before enabling it.",
        "host remove" => "Removes the configured remote definition for ID, not a machine or its workloads.",
        "host status" => "Without ID reads the local host; ID selects a configured enabled remote. No ad-hoc host or command. Unavailable sources return exit 1/available=false/observedAt=null, not healthy zero values.",
        "monitor status" or "monitor watch" => "Options: --task NAME (exact case-insensitive name, not a wildcard). watch also accepts --interval 1..3600 (seconds), --count 1..10000 (snapshots). Agent/host/group availability stays global; empty filtered data does not prove health or existence. Reads existing samples; no workload action.",
        "monitor history" => "Choose a task name or a complete JSON query, never both. Named options: --window 24h|7d|30d and --buckets 1..1000. Missing intervals stay unknown; observed availability is not an SLA.",
        "logs" => "Options: --task NAME OR --source ID; --level LEVEL; --since ISO_WITH_OFFSET; --tail 1..200; --from-start OR --cursor TOKEN; --follow [--interval 1..3600] [--count 1..10000]; --export NEW_LOCAL_FILE. Default source: control:agent. Export writes one bounded NDJSON page, never overwrites and cannot follow. Business files must be configured; WSL journal/Windows Event providers remain unavailable.",
        "logs sources" => "Lists available/configured log sources and their limitations; no implicit workload start.",
        "logs path" => "Queries the configured log directory via the Agent.",
        "logs exceptions" => "Options: --level warning|error|critical; --search TEXT. Legacy GUI snapshot, not configured log_sources: at most 32 files, 256 KiB/240 tail lines per file and 200 entries. Filters apply after the snapshot cap. sourceCount counts attempted files; truncated=false/empty does not prove availability or completeness. No paging/follow/export.",
        "recovery state" => "Reads shared recovery state. Local pending-hint diagnostics on stderr do not change Agent data/exit status or unlock recovery; not an offline/orphan inventory.",
        "recovery resume" => "Explicitly clears the eligible task's recovery pause with revision from recovery state. An unresolved local hint blocks submission; query the original operation after an uncertain result.",
        "autorestart status" => "Reads live auto-restart status, elevated maintenance channel availability, matched process metrics, and last/next scheduled execution for each configured rule.",
        "autorestart run" => "Immediately executes the configured auto-restart rule RULE_ID via the running Agent (works even if the rule is currently disabled). Requires explicit --yes.",
        "daemon start" => "Explicitly starts the root's Agent. Other commands never implicitly perform this action.",
        "daemon status" => "Queries the existing Agent handshake/status without starting it.",
        "daemon stop" => "Stops monitoring, NOT managed workloads. A consumed shutdown receipt does not prove the current Agent stopped.",
        "operation show" => "Queries the original operation receipt in the same root. Historical completion is not current native state and never clears self/recovery pending hints. This legacy query may acquire the workspace lease and clean orphan journal temporary files; startup operation inspect has a separate strictly read-only diagnostic contract.",
        "doctor" => "Returns current diagnostic checks and real failures through the existing Agent; use --json for full data.",
        "startup list" => "Options: --scope all|user|machine; --type all|registry|shortcut; --state all|active|disabled; --search TEXT; --limit 1..200. active means configured, not running. revision describes managed configuration, NOT global Windows startup CAS. Preserve errors, unknown counts and truncation.",
        "startup inspect" => "Read-only. --native opts into schema 2 fixed-slot observations for disabled shortcuts; default is schema 1 metadata review. Build source/name/isDisabled/metadataDigest from one selected startup list entry; use that list's revision. Digest/present slots are not historical identity or write authority. Unknown observations keep diagnostic data with nonzero exit. No --yes/--operation-id; native actions remain blocked.",
        "startup adopt" => "JSON contains expectedEntry (complete selected startup list entry) and task (reviewed task-add form). Rechecks metadata and writes ONLY this root's task configuration; no native changes or explicit execution. --yes acknowledges possible duplicate launch by independent launchers, not exclusive ownership. Re-read/review conflicts, never auto-retry.",
        "startup self status" or "startup boot status" => "Read-only global registration metadata, not binary trust, actual launch or workload liveness. Self enabled remains unknown; boot enabled is the Scheduler setting. No --yes or writes.",
        "startup self prepare" => "Read-only point-in-time preparation for enable or disable. Requires no file/revision/confirmation/operation ID. Save ONLY fresh JSON.data; review enabled/context/fingerprint before self set. Existing/unreadable local pending hints block preparation. Integrity checks are not publisher certification.",
        "startup self set" => "Consumes ONLY reviewed JSON.data from fresh startup self prepare; there is no safe fabricated template. Strict schema 1 requires enabled, confirmed, targetContextId and expectedRegistration (no unknown/duplicate/case-aliased/missing fields). File confirmation never substitutes for this invocation's --yes. Only the current server package's fixed GUI/current actor/root may qualify; no arbitrary exe/key/SID. No auto-prepare, refresh, retry, daemon/UAC or target launch. Existing/unreadable pending hints block submission; never clear or replay them to retry. Reserves the shared pending hint and prints its admitted operation ID on stderr before exchange, also with --json. After timeout/disconnect/unknown retain that ID and query operation show; queries/replay/matching state never clear the hint. Historical success is not current native state; Boot writes stay closed.",
        "startup operation inspect" => "Strictly read-only diagnostic of an existing startup-disable UUIDv7; no replay, native writes, finalization or mutation flags. Exit 0 means complete diagnostic, NOT original success/authority. Unknown/busy/timeout/cancel: 6; missing/unverifiable: 1; permission: 4. Retain partial data when supplied; never upgrade unknown or infer write eligibility.",
        _ => throw new InvalidOperationException($"Missing help for {key}."),
    };

    private static string? SchemaSupplement(string key) => key switch
    {
        "config validate" or "config apply" => YamlSchemaSection,
        "task add" => TaskAddSchemaSection,
        "settings set" => SettingsSetSchemaSection,
        "host add" or "host update" or "host upsert" => HostSchemaSection,
        _ => null,
    };

    internal const string SkillSummarySection = """
        Built-in AI Skill & Configuration Schema (full guide: vigil help skill)
        =======================================================================
        1) Standard AI Agent Workflow (always read fresh revision before writing):
           a. Locate workspace: `vigil config path --json` (uses --root, VIGIL_ROOT, or exe/default root).
           b. Ensure Agent is up: `vigil daemon status --json` (only start explicitly: `vigil daemon start --json`).
           c. Read state + CAS revision: `vigil config show --json` (raw YAML: `vigil config show --raw`).
           d. Validate YAML (`<= 40 KiB`): `vigil config validate --file autorun.yaml --json`.
           e. Commit YAML atomically: `vigil config apply --file autorun.yaml --expected-revision <SHA256> --yes --json`.
              Or mutate single objects via JSON stdin (`--file -`): `task add`, `settings patch/set`, `host add/update/upsert`.
           f. Re-read `revision` on exit 5 (`revision_conflict`). After exit 6 (timeout/unknown), inspect original `--operation-id` via `vigil operation show UUIDv7`.

        2) autorun.yaml Schema (`version: 1` or `2`; top-level `version`, `settings`, `tasks` are REQUIRED):
           version: 1
           settings:
             log_dir: logs                # string, optional (default: logs; relative to root or absolute, env-vars expanded)
             backup_dir: backups          # string, optional (default: backups)
             default_startup_delay: 5     # int >= 0, optional (default: 5 seconds)
           tasks:                         # array (may be `[]`)
             - name: example-app          # string, required, unique case-insensitively
               type: windows              # REQUIRED: windows | windows_service | wsl_boot | wsl_systemd | wsl_command
               enabled: true              # bool, optional (default: true; overridden by autorun.overrides.json)
               expected_running: false    # bool, optional (default: false; tracks continuous liveness)
               start_on_app_launch: null  # bool?, optional (null inherits global auto_start_missing_on_launch)
               auto_recover: null         # bool?, optional (null inherits global auto_recover_failed_tasks)
               description: "Summary"     # string?, optional
               startup_delay: 5           # int >= 0, optional (inherits settings.default_startup_delay)
               depends_on: []             # list<string>, optional (must exist and form a DAG)
               # Required fields by `type`:
               #   windows         -> exec (string); optional args (list<string>), working_dir (string)
               #   windows_service -> service (string, Windows SCM service name)
               #   wsl_boot        -> distro (string, e.g. Ubuntu)
               #   wsl_systemd     -> distro (string) AND service (string)
               #   wsl_command     -> distro (string) AND command (string)
               exec: "C:\\Apps\\Example\\Example.exe"
               args: ["--serve"]
               working_dir: "C:\\Apps\\Example"
               health:                    # REQUIRED object
                 type: process            # REQUIRED: process | tcp | windows_service | wsl_running | systemd_active | wsl_process
                 # Required fields by `health.type`:
                 #   process         -> pattern (process/image name match)
                 #   tcp             -> host (string) AND port (int 1..65535)
                 #   windows_service -> service (or inherits task.service)
                 #   wsl_running     -> distro (or inherits task.distro)
                 #   systemd_active  -> distro AND service (each may inherit from task)
                 #   wsl_process     -> distro (or inherits task.distro) AND pattern (string)
                 pattern: "Example.exe"
                 timeout: 30              # int > 0, optional (default: 30 seconds)
               launch:                    # optional object (defaults depend on `type`)
                 run_as: current_user     # current_user (alias: user) | system
                 trigger: logon           # manual | logon | boot
                 window_mode: hidden      # normal | minimized | hidden | no_console | tray
                 lifecycle: tracked       # detached | tracked | service

        3) JSON Payload DTO Schemas (`--file PATH` or `--file -`):
           - task add (`AddTaskRequest`, camelCase keys):
             `{"name","type","description","executable","arguments":[],"workingDirectory","distro","service","command","dependsOn":[],"startupDelay":5,"healthType","healthPattern","healthHost","healthPort":null,"healthTimeoutSeconds":30,"launch":{"runAs":"current_user","trigger":"logon","windowMode":"hidden","lifecycle":"tracked"},"expectedRunning":false,"startOnAppLaunch":null,"autoRecover":null}`
           - settings patch (`ApplicationSettingsPatch`, camelCase): `{"autoStartMissingOnLaunch":true,"autoRecoverFailedTasks":true}`
           - settings set (`ApplicationSettings`, snake_case): `{"auto_start_missing_on_launch":false,"auto_recover_failed_tasks":false,"remote_hosts":[...],"log_sources":[{"id":"app-log","kind":"file","taskName":null,"path":"logs\\app.log"}],"alert_settings":{...}}`
           - host add/update/upsert (`RemoteHostDefinition`, snake_case): `{"id","name","enabled":false,"platform":"linux|windows","host","port":22,"user_name","authentication":"ssh_agent|private_key","identity_file":null,"known_hosts_file","connect_timeout_seconds":10,"command_timeout_seconds":20}`
        """;

    private const string YamlSchemaSection = """
        Complete `autorun.yaml` Schema (`version: 1` or `2`):
          - Required top-level keys: `version` (1 or 2), `settings` (object; empty `{}` allowed), `tasks` (list; `[]` allowed).
          - `settings`: `log_dir` (default `logs`), `backup_dir` (default `backups`), `default_startup_delay` (int >= 0, default `5`).
          - Each task item:
              name (required, unique case-insensitively), type (windows|windows_service|wsl_boot|wsl_systemd|wsl_command),
              enabled (bool, default true), expected_running (bool, default false),
              start_on_app_launch (bool?), auto_recover (bool?), description (string?),
              startup_delay (int >= 0), depends_on (list<string>, acyclic),
              type fields: exec/args/working_dir (windows) | service (windows_service) | distro (wsl_*) | command (wsl_command),
              health (required): {type: process|tcp|windows_service|wsl_running|systemd_active|wsl_process, pattern, host, port (1..65535), distro, service, timeout (int > 0, default 30)},
              launch (optional): {run_as: current_user|user|system, trigger: manual|logon|boot, window_mode: normal|minimized|hidden|no_console|tray, lifecycle: detached|tracked|service}.
          - Full AI skill & YAML/JSON examples: run `vigil help skill`.
        """;

    private const string TaskAddSchemaSection = """
        Schema (`AddTaskRequest`, camelCase keys; enum values accept snake_case strings):
          Required: name (string), type (windows|windows_service|wsl_boot|wsl_systemd|wsl_command),
                    healthType (process|tcp|windows_service|wsl_running|systemd_active|wsl_process) + type-specific fields:
            windows         -> executable (string), optional arguments (string[]), workingDirectory (string)
            windows_service -> service (string)
            wsl_boot        -> distro (string)
            wsl_systemd     -> distro (string), service (string)
            wsl_command     -> distro (string), command (string)
          Health fields: healthPattern (process/wsl_process), healthHost + healthPort 1..65535 (tcp), healthTimeoutSeconds (>0, default 30).
          Optional flags/deps: description, dependsOn (string[]), startupDelay (int >= 0), expectedRunning (bool), startOnAppLaunch (bool?), autoRecover (bool?).
          Optional launch: {"runAs":"current_user|system","trigger":"manual|logon|boot","windowMode":"normal|minimized|hidden|no_console|tray","lifecycle":"detached|tracked|service"}.
          Full guide: run `vigil help skill`.
        """;

    private const string SettingsSetSchemaSection = """
        Schema (`ApplicationSettings`, snake_case top-level keys):
          auto_start_missing_on_launch (bool), auto_recover_failed_tasks (bool),
          remote_hosts (RemoteHostDefinition[]), log_sources ([{"id":"...","kind":"file","taskName":null,"path":"..."}]),
          alert_settings ({"enabled":true,"cpu_sustain_seconds":60,"host_cpu_percent":85,"host_memory_percent":85,"host_storage_percent":90,"process_cpu_percent":85,"process_memory_percent":25,"process_memory_mb":2048,"host_overrides":{},"task_overrides":{}}).
          For modifying only booleans without touching hosts/logs, prefer `vigil settings patch`.
          Full guide: run `vigil help skill`.
        """;

    private const string HostSchemaSection = """
        Schema (`RemoteHostDefinition`, snake_case keys):
          id (unique token, no spaces), name (non-empty), enabled (bool), platform ("windows"|"linux"),
          host (token, no spaces), port (1..65535, default 22), user_name (token, no spaces),
          authentication ("ssh_agent"|"private_key"), identity_file (required absolute non-reparse file for private_key),
          known_hosts_file (required absolute non-reparse file), connect_timeout_seconds (1..60, default 10), command_timeout_seconds (2..120, default 20).
          Full guide: run `vigil help skill`.
        """;

    private const string SkillText = """
        # Vigil CLI Operator & Configuration Skill (Built-in)

        Use `vigil.exe` when automating, inspecting, or configuring Vigil workspaces on Windows.
        Do NOT write arbitrary guesses to `autorun.yaml` or `settings.json`; read the live configuration and revision first, validate, then commit with CAS protection.

        ## 1. Non-Negotiable Operating Rules
        - Always pass `--json` when scripting so outputs are single-line `schemaVersion=1` envelopes (`{"schemaVersion":1,"ok":...,"revision":...,"data":...,"error":...}`).
        - Discover the active workspace before reading or writing:
          `vigil config path --json`
          Pass `--root <DIR>` explicitly (or rely on `VIGIL_ROOT` / package root). Never let commands implicitly launch UI or UAC.
        - Explicitly verify/start the daemon before live operations:
          `vigil daemon status --json`
          If not running: `vigil daemon start --json` (starts `Vigil.Agent.exe` without GUI).
        - Protect every write with optimistic concurrency (`--expected-revision <SHA256>`) and explicit confirmation (`--yes` where required):
          Obtain `<SHA256>` from `revision` in a fresh `vigil config show --json` (or `startup list --json` / `recovery state --json`).
          On exit code `5` (`revision_conflict` / `startup_entry_changed`), re-read and re-review; NEVER blind-retry.
          On exit code `6` (timeout/unknown outcome), save `--operation-id` (or stderr admitted ID) and query `vigil operation show <UUIDv7> --json`.
        - External direct edits to `autorun.yaml` race the Agent lock; prefer `vigil config show --raw` -> edit -> `vigil config validate` -> `vigil config apply --yes`.

        ## 2. Complete `autorun.yaml` Schema (`version: 1`)
        Constraints enforced by `ConfigurationLoader`:
        - Max size: 40 KiB UTF-8 YAML.
        - Top-level keys `version` (`1` or `2`), `settings` (object, `{}` minimum), and `tasks` (list, `[]` minimum) are ALL REQUIRED.
        - Unknown keys are ignored cleanly by the loader (`IgnoreUnmatchedProperties`); duplicate YAML keys fail validation.

        ```yaml
        version: 1
        settings:
          log_dir: logs                # optional string (default: logs; relative to workspace root or absolute; %ENV% expanded)
          backup_dir: backups          # optional string (default: backups)
          default_startup_delay: 5     # optional int >= 0 (default: 5 seconds)
        tasks:
          # Example A: Windows GUI/background executable with TCP health check
          - name: example-api
            type: windows              # windows | windows_service | wsl_boot | wsl_systemd | wsl_command
            enabled: true              # optional bool (default: true; autorun.overrides.json overrides this if present)
            expected_running: true     # optional bool (default: false; true = monitor/recover while running)
            start_on_app_launch: true  # optional bool? (null = inherit global auto_start_missing_on_launch)
            auto_recover: true         # optional bool? (null = inherit global auto_recover_failed_tasks)
            description: "Local HTTP API"
            startup_delay: 3           # optional int >= 0 (default: settings.default_startup_delay)
            depends_on: []             # optional list<string> of task names (must be acyclic)
            exec: "C:\\Apps\\Api\\api.exe"     # REQUIRED for type: windows (%ENV% supported)
            args: ["--host", "127.0.0.1"]      # optional list<string> (windows)
            working_dir: "C:\\Apps\\Api"       # optional string (windows)
            health:                            # REQUIRED object for every task
              type: tcp                        # process | tcp | windows_service | wsl_running | systemd_active | wsl_process
              host: "127.0.0.1"                # REQUIRED for health.type: tcp
              port: 8080                       # REQUIRED int 1..65535 for health.type: tcp
              timeout: 30                      # optional int > 0 (default: 30 seconds)
            launch:                            # optional object
              run_as: current_user             # current_user (alias: user) | system
              trigger: logon                   # manual | logon | boot
              window_mode: hidden              # normal | minimized | hidden | no_console | tray
              lifecycle: tracked               # detached | tracked | service

          # Example B: Windows Service (`type: windows_service`)
          - name: postgresql-svc
            type: windows_service
            service: "postgresql-x64-16"       # REQUIRED for type: windows_service
            expected_running: true
            health:
              type: windows_service            # service inherits from task.service unless overridden
              timeout: 30
            launch:
              trigger: boot
              lifecycle: service

          # Example C: WSL Distro Boot (`type: wsl_boot`)
          - name: wsl-ubuntu
            type: wsl_boot
            distro: "Ubuntu-24.04"             # REQUIRED for type: wsl_boot / wsl_systemd / wsl_command
            expected_running: true
            health:
              type: wsl_running                # distro inherits from task.distro unless overridden
              timeout: 45

          # Example D: WSL systemd Service (`type: wsl_systemd`)
          - name: wsl-docker
            type: wsl_systemd
            distro: "Ubuntu-24.04"
            service: "docker.service"          # REQUIRED for type: wsl_systemd
            depends_on: ["wsl-ubuntu"]
            health:
              type: systemd_active             # distro & service inherit from task unless overridden
              timeout: 45

          # Example E: WSL Command (`type: wsl_command`)
          - name: wsl-worker
            type: wsl_command
            distro: "Ubuntu-24.04"
            command: "cd /srv/worker && ./start.sh"   # REQUIRED for type: wsl_command
            depends_on: ["wsl-ubuntu"]
            health:
              type: wsl_process
              pattern: "worker"                # REQUIRED for process / wsl_process
              timeout: 30
        ```

        ## 3. All Mutating JSON Input Schemas (`--file PATH` or `--file -`)
        Enums in JSON accept `snake_case` strings (`windows_service`, `ssh_agent`, `no_console`, etc.).

        ### `vigil task add --file task.json --expected-revision <SHA256> --json`
        DTO `AddTaskRequest` (camelCase properties):
        ```json
        {
          "name": "example-app",
          "type": "windows",
          "description": "Background worker",
          "executable": "C:\\Apps\\Example\\Example.exe",
          "arguments": ["--port", "8080"],
          "workingDirectory": "C:\\Apps\\Example",
          "distro": null,
          "service": null,
          "command": null,
          "dependsOn": [],
          "startupDelay": 5,
          "healthType": "process",
          "healthPattern": "Example.exe",
          "healthHost": null,
          "healthPort": null,
          "healthTimeoutSeconds": 30,
          "launch": {
            "runAs": "current_user",
            "trigger": "logon",
            "windowMode": "hidden",
            "lifecycle": "tracked"
          },
          "expectedRunning": true,
          "startOnAppLaunch": true,
          "autoRecover": true
        }
        ```

        ### `vigil settings patch --file - --expected-revision <SHA256> --json`
        DTO `ApplicationSettingsPatch` (camelCase properties; preserves `remote_hosts` & `log_sources`):
        ```json
        {"autoStartMissingOnLaunch": true, "autoRecoverFailedTasks": true}
        ```

        ### `vigil settings set --file settings.json --expected-revision <SHA256> --json`
        DTO `ApplicationSettings` (snake_case properties; REPLACES entire preferences object — read `settings get --json` first and merge `data.preferences`):
        ```json
        {
          "auto_start_missing_on_launch": true,
          "auto_recover_failed_tasks": true,
          "remote_hosts": [],
          "log_sources": [
            {"id": "api-log", "kind": "file", "taskName": "example-api", "path": "logs\\api.log"}
          ],
          "alert_settings": {
            "enabled": true,
            "cpu_sustain_seconds": 60,
            "host_cpu_percent": 85,
            "host_memory_percent": 85,
            "host_storage_percent": 90,
            "process_cpu_percent": 85,
            "process_memory_percent": 25,
            "process_memory_mb": 2048,
            "host_overrides": {
              "local": {"cpu_percent": 90, "memory_percent": 90, "storage_percent": 95}
            },
            "task_overrides": {
              "example-api": {"cpu_percent": 75, "memory_percent": 30, "memory_mb": 4096}
            }
          }
        }
        ```
        Note on `log_sources`: `kind` must be `file` (valid local DOS/root-relative path, max 32 sources); `wsl-journal` and `windows-event` reject paths and remain runtime-unavailable.

        ### `vigil host add|update|upsert --file host.json --expected-revision <SHA256> --json`
        DTO `RemoteHostDefinition` (snake_case properties; strictly read-only SSH observation via Windows OpenSSH):
        ```json
        {
          "id": "home-nas",
          "name": "Home NAS",
          "enabled": true,
          "platform": "linux",
          "host": "192.0.2.25",
          "port": 22,
          "user_name": "ops",
          "authentication": "ssh_agent",
          "identity_file": null,
          "known_hosts_file": "C:\\Keys\\known_hosts",
          "connect_timeout_seconds": 10,
          "command_timeout_seconds": 20
        }
        ```
        Notes: `platform` = `linux` | `windows`; `authentication` = `ssh_agent` | `private_key` (if `private_key`, `identity_file` must be an existing absolute non-reparse file). `known_hosts_file` must be an existing absolute non-reparse file.

        ## 4. Recommended End-to-End Recipe (PowerShell)
        ```powershell
        # 1. Start Agent if needed & snapshot workspace revision
        $status = vigil daemon status --json | ConvertFrom-Json
        if (-not $status.ok) { vigil daemon start --json | Out-Null }
        $cfg = vigil config show --json | ConvertFrom-Json

        # 2a. Apply edited autorun.yaml
        vigil config validate --file autorun.yaml --json
        vigil config apply --file autorun.yaml --expected-revision $cfg.revision --yes --json

        # 2b. Or add one task cleanly from JSON stdin
        @'
        {"name":"my-app","type":"windows","executable":"C:\\Tools\\MyApp.exe","arguments":[],"healthType":"process","healthPattern":"MyApp.exe","expectedRunning":true}
        '@ | vigil task add --file - --expected-revision $cfg.revision --json
        ```

        ## 5. Exit Codes Reference
        - `0`: success (`diagnostic 0` on `startup operation inspect` is historical diagnostic completion, NOT authority).
        - `1`: execution / partial failure / unavailable source.
        - `2`: invalid argument / YAML or JSON schema validation error (`configuration_error` / `invalid_argument`).
        - `3`: Agent connection / IPC protocol error / pending storage unavailable.
        - `4`: permission denied or missing `--yes` confirmation.
        - `5`: revision conflict (`revision_conflict` / `startup_entry_changed`) — reload fresh state and re-review.
        - `6`: timeout / cancelled / busy / uncertain outcome — inspect admitted `--operation-id` via `vigil operation show <UUIDv7>`.
        """;

    private static string? Example(string key) => key switch
    {
        "task add" => """{"name":"example-app","type":"windows","executable":"C:\\Apps\\Example\\Example.exe","arguments":[],"expectedRunning":false}""",
        "settings patch" => """{"autoRecoverFailedTasks":false,"autoStartMissingOnLaunch":false}""",
        "host add" or "host update" or "host upsert" => """{"id":"example-host","name":"Example","enabled":false,"platform":"linux","host":"192.0.2.25","port":22,"user_name":"example","authentication":"ssh_agent","known_hosts_file":"C:\\Keys\\known_hosts"}""",
        _ => null,
    };
}
