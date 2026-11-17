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
        text.AppendLine("Global: --root DIR --json --timeout SECONDS (>0..600, default 30) --non-interactive --help --version");
        text.AppendLine("Help is local: no root resolution, stdin/file reads, Agent connection, daemon start or UAC. Quote names with spaces; -- ends option parsing.");
        text.AppendLine("Exit: 0 success; 1 execution/partial failure; 2 input; 3 connection/protocol; 4 permission/confirmation; 5 conflict; 6 timeout/cancel/unknown. Ctrl+C ends watch/follow with 0.");
        text.AppendLine("Use vigil help COMMAND for a command, or vigil --help for all contracts and coverage gaps.");
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

    private static string? Example(string key) => key switch
    {
        "task add" => """{"name":"example-app","type":"windows","executable":"C:\\Apps\\Example\\Example.exe","arguments":[],"expectedRunning":false}""",
        "settings patch" => """{"autoRecoverFailedTasks":false,"autoStartMissingOnLaunch":false}""",
        "host add" or "host update" or "host upsert" => """{"id":"example-host","name":"Example","enabled":false,"platform":"linux","host":"192.0.2.25","port":22,"user_name":"example","authentication":"ssh_agent","known_hosts_file":"C:\\Keys\\known_hosts"}""",
        _ => null,
    };
}
