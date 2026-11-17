using System.Globalization;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Cli;

public sealed record CliRoute(string Key, string? Rpc, bool NeedsName = false, Type? PayloadType = null,
    bool Revision = false, bool Confirmation = false, bool? Flag = null);

public sealed record CliArguments(CliRoute Route, string? Name, IReadOnlyDictionary<string, string> Options, bool Json)
{
    public string? Option(string key) => Options.GetValueOrDefault(key);
    public double TimeoutSeconds => double.Parse(Option("timeout") ?? "30", CultureInfo.InvariantCulture);
    public StartupListQuery StartupQuery()
    {
        if (!int.TryParse(Option("limit") ?? "100", NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
        { throw new ArgumentException("Startup --limit must be 1..200."); }
        return new StartupListQuery(Option("scope") ?? "all", Option("type") ?? "all", Option("state") ?? "all",
            Option("search"), limit).Validate();
    }
    public static IReadOnlyList<CliRoute> Routes { get; } =
    [
        new("task list", "list"), new("task show", "status", true),
        new("task start", "start", true), new("task stop", "stop", true), new("task restart", "restart", true),
        new("task start-missing", "start-missing"),
        new("task add", "task-add", PayloadType: typeof(AddTaskRequest), Revision: true),
        new("task remove", "task-remove", true, Revision: true, Confirmation: true),
        new("task enable", "task-enable", true, Revision: true, Flag: true),
        new("task disable", "task-enable", true, Revision: true, Flag: false),
        new("task track", "task-monitor", true, Revision: true, Flag: true),
        new("task untrack", "task-monitor", true, Revision: true, Flag: false),
        new("config show", "configuration"), new("config path", null),
        new("config validate", "configuration-validate", PayloadType: typeof(ConfigurationDocumentRequest)),
        new("config apply", "configuration-apply", PayloadType: typeof(ConfigurationDocumentRequest), Revision: true, Confirmation: true),
        new("settings get", "settings-get"),
        new("settings set", "settings-set", PayloadType: typeof(ApplicationSettings), Revision: true),
        new("settings patch", "settings-patch", PayloadType: typeof(ApplicationSettingsPatch), Revision: true),
        new("host list", "configuration"), new("host show", "configuration", true),
        new("host upsert", "remote-upsert", PayloadType: typeof(RemoteHostDefinition), Revision: true),
        new("host add", "remote-add", PayloadType: typeof(RemoteHostDefinition), Revision: true),
        new("host update", "remote-update", PayloadType: typeof(RemoteHostDefinition), Revision: true),
        new("host remove", "remote-remove", true, Revision: true, Confirmation: true),
        new("host status", "host-status"),
        new("monitor status", "monitoring-snapshot"), new("monitor watch", "monitoring-snapshot"),
        new("monitor history", "monitoring-history"),
        new("recovery state", "recovery-state"),
        new("recovery resume", "recovery-resume", true, Revision: true, Confirmation: true),
        new("daemon status", "ping"), new("daemon start", null), new("daemon stop", "shutdown"),
        new("operation show", "operation", true), new("doctor", "doctor"),
        new("startup operation inspect", "startup-operation-inspect", true),
        new("logs sources", "log-sources"), new("logs path", "configuration"),
        new("logs exceptions", "exception-logs"), new("logs", "logs"),
        new("startup list", "startup-list"),
        new("startup inspect", "startup-inspect", true, typeof(StartupInspectionRequest), Revision: true),
        new("startup adopt", "startup-adopt", true, typeof(StartupAdoptionRequest), Revision: true, Confirmation: true),
        new("startup self status", "startup-self-status"), new("startup boot status", "startup-boot-status"),
        new("startup self prepare", "startup-self-prepare", true),
        new("startup self set", "startup-self-set", PayloadType: typeof(SelfStartupMutationRequest), Confirmation: true),
    ];

    public static CliArguments Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var words = new List<string>();
        var literals = false;
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!literals && token == "--") { literals = true; continue; }
            if (literals || !token.StartsWith("--", StringComparison.Ordinal)) { words.Add(token); continue; }
            var pair = token[2..].Split('=', 2);
            var flag = pair[0] is "json" or "yes" or "non-interactive" or "help" or "version" or "follow" or "from-start" or "raw" or "native";
            if (!flag && pair[0] is not ("root" or "timeout" or "expected-revision" or "operation-id" or "file"
                or "window" or "buckets" or "interval" or "count" or "source" or "task" or "level" or "since" or "tail" or "cursor" or "export"
                or "scope" or "type" or "state" or "search" or "limit")) { throw new ArgumentException($"Unknown option --{pair[0]}."); }
            if (flag && pair.Length != 1) { throw new ArgumentException($"--{pair[0]} does not take a value."); }
            var value = flag ? "true" : pair.Length == 2 ? pair[1]
                : ++index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index]
                : throw new ArgumentException($"--{pair[0]} needs a value.");
            if (string.IsNullOrWhiteSpace(value) || !options.TryAdd(pair[0], value)) { throw new ArgumentException($"Empty or duplicate --{pair[0]}."); }
        }
        var json = options.ContainsKey("json");
        var helpCommand = words.Count != 0 && words[0].Equals("help", StringComparison.OrdinalIgnoreCase);
        if (options.ContainsKey("help") || helpCommand || words.Count == 0 && !options.ContainsKey("version"))
        {
            var topic = CliHelp.ResolveTopic(helpCommand ? words.Skip(1).ToArray() : words);
            return new(new("help", null), topic, options, json);
        }
        if (options.ContainsKey("version")) { return new(new("version", null), null, options, json); }
        var route = Routes.FirstOrDefault(item => words.Count >= item.Key.Split(' ').Length
            && string.Join(' ', words.Take(item.Key.Split(' ').Length)).Equals(item.Key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown command. Run vigil --help.");
        var rest = words.Skip(route.Key.Split(' ').Length).ToArray();
        var history = route.Key == "monitor history";
        if (rest.Length > (route.NeedsName || history || route.Key == "host status" ? 1 : 0) || route.NeedsName && rest.Length != 1
            || history && rest.Length != 1 && !options.ContainsKey("file"))
        { throw new ArgumentException($"Invalid arguments for {route.Key}."); }
        if (options.ContainsKey("native"))
        {
            if (route.Key != "startup inspect") { throw new ArgumentException("--native only applies to startup inspect; it observes disabled shortcut slots without write authority."); }
            route = route with { Rpc = "startup-inspect-native" };
        }
        var result = new CliArguments(route, rest.FirstOrDefault(), options, json);
        if (route.Key is "monitor status" or "monitor watch")
        {
            if (options.Keys.Any(key => key is not ("root" or "json" or "timeout" or "non-interactive" or "task")
                && !(route.Key == "monitor watch" && key is "count" or "interval")))
            { throw new ArgumentException("Monitoring accepts read-only transport options, local --task and watch --count/--interval only; no payload or mutation options."); }
        }
        if (route.Key == "logs exceptions")
        {
            if (options.Keys.Any(key => key is not ("root" or "json" or "timeout" or "non-interactive" or "level" or "search")))
            { throw new ArgumentException("Legacy exceptions accepts read-only transport options and local --level/--search only; no payload, revision, confirmation, operation ID or paging/export."); }
            if (result.Option("level") is { } level && !new[] { "warning", "error", "critical" }.Contains(level, StringComparer.OrdinalIgnoreCase))
            { throw new ArgumentException("Exception --level must be warning, error or critical."); }
        }
        if (route.Key == "startup self prepare")
        {
            if (result.Name is not ("enable" or "disable") || options.Keys.Any(key => key is not ("root" or "json" or "timeout" or "non-interactive")))
            { throw new ArgumentException("Self preparation accepts enable or disable and read-only transport options only; no confirmation, payload, revision or operation ID."); }
        }
        if (route.Key == "startup operation inspect")
        {
            if (options.Keys.Any(key => key is not ("root" or "json" or "timeout" or "non-interactive")))
            { throw new ArgumentException("Operation inspection only accepts --root, --json, --timeout and --non-interactive; no payload, revision or mutation options."); }
            result = result with { Name = Core.Ipc.OperationJournal.NormalizeId(result.Name!) };
        }
        if (!double.TryParse(result.Option("timeout") ?? "30", NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout)
            || !double.IsFinite(timeout) || timeout <= 0 || timeout > 600) { throw new ArgumentException("--timeout must be >0 and <=600 seconds."); }
        var rev = result.Option("expected-revision");
        if (route.Revision && rev is null) { throw new ArgumentException(route.Key == "startup inspect"
            ? "Inspection requires --expected-revision from startup list."
            : "This change requires --expected-revision from config show (recovery resume: recovery state)."); }
        if (rev is not null && (!route.Revision || rev.Length != 64 || !rev.All(char.IsAsciiHexDigit)))
        { throw new ArgumentException("Unexpected or invalid --expected-revision; use a 64-character SHA256 revision."); }
        if (route.Confirmation && !options.ContainsKey("yes"))
        { throw new CliAuthorizationException("This operation requires explicit --yes; no interactive prompt is opened."); }
        if (route.PayloadType is not null && result.Option("file") is null) { throw new ArgumentException("This command requires --file PATH (or --file - for stdin; config validate/apply use YAML, others use JSON)."); }
        if (options.ContainsKey("raw") && route.Key != "config show") { throw new ArgumentException("--raw only applies to config show."); }
        if (options.ContainsKey("file") && route.PayloadType is null && !history) { throw new ArgumentException("--file is not valid for this command."); }
        if (history && options.ContainsKey("file") && rest.Length != 0) { throw new ArgumentException("Use either a history task name or --file, not both."); }
        foreach (var key in new[] { "window", "buckets" })
        {
            if (options.ContainsKey(key) && (!history || options.ContainsKey("file"))) { throw new ArgumentException($"--{key} only applies to named history queries."); }
        }
        if (result.Option("window") is { } window && window is not ("24h" or "7d" or "30d")) { throw new ArgumentException("--window must be 24h, 7d, or 30d."); }
        CheckNumber("buckets", 1, 1000); CheckNumber("count", 1, 10000); CheckNumber("interval", 1, 3600); CheckNumber("tail", 1, 200);
        foreach (var key in new[] { "follow", "from-start", "source", "task", "level", "since", "tail", "cursor", "export" })
        { if (options.ContainsKey(key) && route.Key != "logs" && !(route.Key == "logs exceptions" && key == "level")
                && !(key == "task" && route.Key is "monitor status" or "monitor watch"))
            { throw new ArgumentException(key == "task" ? "--task only applies to logs or monitor status/watch." : $"--{key} only applies to logs."); } }
        if (options.ContainsKey("from-start") && options.ContainsKey("cursor"))
        { throw new ArgumentException("Choose --from-start for a new read, or --cursor to continue one."); }
        if (options.ContainsKey("follow") && options.ContainsKey("export"))
        { throw new ArgumentException("--export writes one bounded snapshot; use it without --follow."); }
        if (result.Option("since") is { } since && (since.Length > 64
            || !System.Text.RegularExpressions.Regex.IsMatch(since, "(?:Z|[+-][0-9]{2}:[0-9]{2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        { throw new ArgumentException("--since requires an ISO timestamp with Z or an explicit UTC offset."); }
        if (route.Key == "logs")
        {
            Core.Logging.LogPageReader.Validate(new(result.Option("source"), result.Option("task"), result.Option("level"),
                Tail: int.Parse(result.Option("tail") ?? "100", CultureInfo.InvariantCulture), Cursor: result.Option("cursor")));
        }
        foreach (var key in new[] { "scope", "type", "state", "search", "limit" })
        { if (options.ContainsKey(key) && route.Key != "startup list" && !(route.Key == "logs exceptions" && key == "search")) { throw new ArgumentException($"--{key} only applies to startup list."); } }
        if (route.Key == "startup list") { _ = result.StartupQuery(); }
        if (route.Key == "startup inspect" && options.ContainsKey("yes"))
        { throw new ArgumentException("Startup inspection is read-only; --yes does not apply."); }
        if (route.Rpc is "startup-self-status" or "startup-boot-status" && options.ContainsKey("yes"))
        { throw new ArgumentException("Registration status is read-only; --yes does not apply."); }
        foreach (var key in new[] { "count", "interval" })
        { if (options.ContainsKey(key) && route.Key != "monitor watch" && !(route.Key == "logs" && options.ContainsKey("follow"))) { throw new ArgumentException($"--{key} only applies to monitor watch or logs --follow."); } }
        if (options.ContainsKey("operation-id") && (route.Rpc is null || !Core.Ipc.AgentProtocol.IsMutation(route.Rpc)))
        { throw new ArgumentException("--operation-id applies only to Agent mutations."); }
        return result;

        void CheckNumber(string key, int min, int max)
        { if (result.Option(key) is { } value && (!int.TryParse(value, out var number) || number < min || number > max)) { throw new ArgumentException($"--{key} must be {min}..{max}."); } }
    }
}

public sealed class CliAuthorizationException(string message) : InvalidOperationException(message);
