using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Cli;

public sealed record CliRequest(string Command, string? Name, object? Payload, TimeSpan Timeout,
    string? Revision = null, string? OperationId = null);

public interface ICliTransport
{
    Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken);
}

public sealed partial class CliApplication
{
    private readonly Func<string, ICliTransport> _connect;
    private readonly Func<ProjectPaths, CancellationToken, Task> _start;
    public CliApplication(Func<string, ICliTransport>? connect = null, Func<ProjectPaths, CancellationToken, Task>? startDaemon = null)
    {
        _connect = connect ?? (root => new PipeTransport(new AgentClient(root)));
        _start = startDaemon ?? ((paths, token) =>
        {
            ChildProcessIo.PreventCaptureHandleInheritance();
            return new AgentDaemonLauncher(new AgentClient(paths.RootDirectory), paths).EnsureRunningAsync(token);
        });
    }

    public async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        string? operationId = null; string? rootId = null; var inspectionRequested = false; var selfPreparationRequested = false; var selfMutationRequested = false;
        var streaming = false;
        try
        {
            var parsed = CliArguments.Parse(args); json = parsed.Json;
            streaming = parsed.Route.Key == "monitor watch" || parsed.Route.Key == "logs" && parsed.Option("follow") is not null;
            inspectionRequested = parsed.Route.Key is "startup inspect" or "startup operation inspect";
            selfPreparationRequested = parsed.Route.Rpc == "startup-self-prepare";
            selfMutationRequested = parsed.Route.Rpc == "startup-self-set";
            if (parsed.Route.Key is "help" or "version")
            {
                var data = parsed.Route.Key == "help" ? new { text = CliHelp.Render(parsed.Name), version = Version } : new { text = Version, version = Version };
                if (json) { await CliOutput.WriteAsync(output, error, true, new(1, null, null, true, data, null, null, null, null)); }
                else { await output.WriteLineAsync(data.text); }
                return 0;
            }
            var timeout = TimeSpan.FromSeconds(parsed.TimeoutSeconds);
            var paths = ProjectPaths.Resolve(parsed.Option("root"));
            var endpoint = AgentProtocol.ForRoot(paths.RootDirectory);
            rootId = endpoint.RootId;
            if (parsed.Route.Key == "config path")
            {
                await CliOutput.WriteAsync(output, error, json, new(1, null, null, true,
                    new { root = paths.RootDirectory, configuration = paths.ConfigPath }, null, null, DateTimeOffset.UtcNow, rootId));
                return 0;
            }
            var rpc = parsed.Route.Key == "host status" && parsed.Name is not null ? "remote-status"
                : parsed.Route.Key == "config show" && parsed.Option("raw") is not null ? "configuration-document" : parsed.Route.Rpc ?? "ping";
            if (rpc == "recovery-resume") { await RequireNoRecoveryPendingAsync(paths, parsed.Name!, cancellationToken); }
            object? payload = parsed.Route.Flag is { } flag ? new { enabled = flag } : null;
            if (parsed.Option("file") is { } yamlFile && parsed.Route.PayloadType == typeof(ConfigurationDocumentRequest))
            {
                payload = (await ReadInputWithTimeoutAsync(token => ReadYamlInputAsync(yamlFile, input, token), timeout, cancellationToken))
                    with { Confirmed = parsed.Option("yes") is not null };
            }
            else if (parsed.Option("file") is { } file)
            {
                var source = await ReadInputWithTimeoutAsync(token => ReadInputAsync(file, input, token), timeout, cancellationToken);
                var type = parsed.Route.PayloadType ?? typeof(MonitoringHistoryQuery);
                var strict = new JsonSerializerOptions(AgentProtocol.JsonOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
                if (type == typeof(SelfStartupMutationRequest))
                {
                    // Case/duplicate/required fields use the shared dedicated reader,
                    // NOT the permissive generic options. Only this invocation confirms.
                    var self = SelfStartupMutationContract.ReadRequest(source) with { Confirmed = parsed.Option("yes") is not null };
                    if (SelfStartupMutationPolicy.ValidateRequest(self) is not null) { throw new ArgumentException("Invalid self preparation input; use JSON.data from a fresh self prepare response."); }
                    payload = self;
                }
                else
                {
                    if (type == typeof(RemoteHostDefinition))
                    {
                        using var document = JsonDocument.Parse(source);
                        if (!document.RootElement.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
                        { throw new ArgumentException("Host writes require an explicit stable id in the JSON payload."); }
                    }
                    payload = JsonSerializer.Deserialize(source, type, strict) ?? throw new ArgumentException("Input JSON cannot be null.");
                }
                if (payload is StartupInspectionRequest inspection)
                {
                    if (rpc == "startup-inspect-native") { _ = StartupNativeInspectionPolicy.ValidateRequest(parsed.Name!, inspection); }
                    else { _ = inspection.Validate(parsed.Name!); }
                }
                if (payload is StartupAdoptionRequest adoption)
                {
                    adoption = adoption with { Confirmed = parsed.Option("yes") is not null };
                    _ = adoption.Validate(parsed.Name!);
                    payload = adoption;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (AgentProtocol.IsMutation(rpc) && !selfMutationRequested) { operationId = OperationJournal.NormalizeId(parsed.Option("operation-id") ?? AgentProtocol.NewOperationId()); }
            if (selfPreparationRequested)
            {
                var pending = await new SelfStartupPendingStore(paths).ReadAsync(cancellationToken);
                if (pending.BlocksSubmission) { throw SelfPendingBlocked(pending); }
            }
            if (selfMutationRequested)
            { operationId = OperationJournal.NormalizeId(parsed.Option("operation-id") ?? AgentProtocol.NewOperationId()); }
            // Protected mutation factories are deferred until durable publication
            // and diagnostic flush, including arbitrary injected factories.
            var client = selfMutationRequested || rpc == "recovery-resume" ? null : _connect(paths.RootDirectory);
            if (parsed.Route.Key == "daemon start")
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout);
                try { await _start(paths, deadline.Token); }
                catch (OperationCanceledException exception)
                { throw new TimeoutException("Daemon readiness was not confirmed; it may be running. Query daemon status before retrying.", exception); }
            }
            if (parsed.Route.Key == "monitor history" && payload is null)
            {
                var configuration = await client!.SendAsync(new("configuration", null, null, timeout), cancellationToken);
                if (!configuration.Ok)
                {
                    throw new AgentCommandException(configuration.ErrorCode ?? "command_failed", configuration.Error ?? "Configuration query failed.",
                        configuration.Revision, configuration.OperationId, configuration.Id);
                }
                var workspace = Element(configuration.Data).Deserialize<ConfigurationWorkspaceSnapshot>(AgentProtocol.JsonOptions)
                    ?? throw new JsonException("Invalid configuration response.");
                var task = workspace.Configuration.GetTask(parsed.Name!);
                var to = DateTimeOffset.UtcNow;
                var days = parsed.Option("window") switch { "7d" => 7, "30d" => 30, _ => 1 };
                payload = new MonitoringHistoryQuery(task.Name, MonitoringTaskIdentity.For(task), to.AddDays(-days), to,
                    int.Parse(parsed.Option("buckets") ?? "120", System.Globalization.CultureInfo.InvariantCulture));
            }
            LogQuery? logQuery = null;
            if (parsed.Route.Key == "logs")
            {
                logQuery = new LogQuery(parsed.Option("source"), parsed.Option("task"), parsed.Option("level"),
                    parsed.Option("since") is { } since ? DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture) : null,
                    int.Parse(parsed.Option("tail") ?? "100", System.Globalization.CultureInfo.InvariantCulture), parsed.Option("cursor"), parsed.Option("from-start") is not null);
                payload = logQuery;
            }
            var startupQuery = parsed.Route.Key == "startup list" ? parsed.StartupQuery() : null;
            if (startupQuery is not null) { payload = startupQuery; }
            var queryName = rpc == "operation" ? OperationJournal.NormalizeId(parsed.Name) : parsed.Name;
            var request = new CliRequest(rpc, parsed.Route.NeedsName || rpc == "remote-status" ? queryName : null, payload, timeout,
                parsed.Option("expected-revision"), operationId);
            var count = int.Parse(parsed.Option("count") ?? "10000", System.Globalization.CultureInfo.InvariantCulture);
            for (var index = 0; ; index++)
            {
                // A candidate is not recovery evidence. Protected failures supply
                // only a stored original ID or this exchange's reserved ID.
                if (selfMutationRequested || rpc == "recovery-resume") { operationId = null; }
                var response = selfMutationRequested
                    ? await SendSelfAsync(paths, endpoint.RootId, request, () => _connect(paths.RootDirectory), error, cancellationToken)
                    : rpc == "recovery-resume"
                        ? await SendRecoveryResumeAsync(paths, endpoint.RootId, request, () => _connect(paths.RootDirectory), error, cancellationToken)
                        : await client!.SendAsync(request, cancellationToken);
                if (rpc == "recovery-state" && response.Ok)
                {
                    var state = AgentRecoveryClient.ReadState(response, endpoint.RootId);
                    await WriteRecoveryPendingDiagnosticsAsync(paths, endpoint.RootId, state, error, cancellationToken);
                }
                object? data = response.Data;
                if (selfPreparationRequested)
                { data = AgentSelfStartupClient.ReadPreparation(response, parsed.Name == "enable", endpoint.RootDirectory); }
                if (response.Ok && parsed.Route.Key is ("host list" or "host show"))
                {
                    var workspace = Element(data).Deserialize<ConfigurationWorkspaceSnapshot>(AgentProtocol.JsonOptions)
                        ?? throw new JsonException("Invalid configuration response.");
                    data = parsed.Route.Key == "host list" ? workspace.Preferences.RemoteHosts
                        : workspace.Preferences.RemoteHosts.FirstOrDefault(host => host.Id.Equals(parsed.Name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new KeyNotFoundException($"Remote host '{parsed.Name}' does not exist.");
                }
                if (response.Ok && parsed.Route.Key == "logs path")
                {
                    var workspace = Element(data).Deserialize<ConfigurationWorkspaceSnapshot>(AgentProtocol.JsonOptions)
                        ?? throw new JsonException("Invalid configuration response.");
                    data = new { logDirectory = workspace.Configuration.Settings.LogDirectory };
                }
                if (response.Ok && parsed.Route.Key == "logs exceptions")
                {
                    var snapshot = ReadExceptionLogs(data, response.Id);
                    var search = parsed.Option("search")?.Trim(); var level = parsed.Option("level");
                    data = snapshot with { Entries = snapshot.Entries.Where(entry =>
                        (level is null || entry.Level.ToString().Equals(level, StringComparison.OrdinalIgnoreCase))
                        && (string.IsNullOrWhiteSpace(search) || entry.Source.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                            || entry.Message.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                            || entry.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToArray() };
                }
                if (response.Ok && parsed.Route.Key is ("monitor status" or "monitor watch") && parsed.Option("task") is { } selectedTask)
                { data = FilterMonitoringTask(data, selectedTask, response); }
                var element = Element(data);
                if (rpc == "operation")
                {
                    data = response.Ok ? AgentOperationClient.ReadReceipt(response, parsed.Name!) : null;
                    element = Element(data); // Error envelopes never carry trusted historical evidence.
                }
                StartupOperationInspectionSnapshot? operationInspection = null;
                if (rpc == "startup-operation-inspect")
                {
                    operationInspection = AgentStartupClient.ReadOperationInspection(response, parsed.Name!);
                    data = operationInspection; element = Element(data);
                }
                LogPage? logPage = null;
                if (response.Ok && parsed.Route.Key == "logs")
                {
                    logPage = element.Deserialize<LogPage>(AgentProtocol.JsonOptions) ?? throw new JsonException("Invalid log page.");
                    if (logPage.SchemaVersion != 1 || logPage.Entries is null || logPage.Source is null || logPage.Available && string.IsNullOrWhiteSpace(logPage.NextCursor))
                    { throw new JsonException("Incompatible or incomplete log page."); }
                }
                RemoteHostProbeResult? remote = null;
                if (response.Ok && rpc == "remote-status")
                {
                    remote = AgentHostClient.ReadObservation(element, parsed.Name!);
                }
                StartupNativeInspectionSnapshot? nativeInspection = null;
                if (payload is StartupInspectionRequest inspected)
                {
                    // Native errors also go through the typed reader: its exception preserves
                    // identity/code/revision, but never exposes evidence carried by a failed reply.
                    if (rpc == "startup-inspect-native")
                    { nativeInspection = AgentStartupClient.ReadNativeInspection(response, parsed.Name!, inspected, parsed.Option("expected-revision")!); }
                    else if (response.Ok) { _ = AgentStartupClient.ReadInspection(response, parsed.Name!, inspected, parsed.Option("expected-revision")!); }
                }
                if (response.Ok && payload is StartupAdoptionRequest adopted)
                { _ = AgentStartupClient.ReadAdoptionReceipt(response, parsed.Name!, adopted.Task.Name, operationId!); }
                var startup = response.Ok && startupQuery is not null
                    ? AgentStartupClient.ReadObservation(element, startupQuery) : null;
                var registrationKind = rpc switch { "startup-self-status" => "self", "startup-boot-status" => "boot", _ => null };
                var registration = response.Ok && registrationKind is not null
                    ? AgentStartupRegistrationClient.ReadObservation(element, registrationKind, endpoint.RootDirectory) : null;
                var executionFailure = CliOutput.HasExecutionFailure(element);
                // A historical shutdown receipt must never claim this daemon was stopped.
                // Keep the original data/replay marker; do NOT repeat its side effect.
                var historicalShutdown = parsed.Route.Key == "daemon stop" && response.Ok && response.Replayed;
                var diagnosticFailure = operationInspection is null ? null : StartupOperationInspectionPolicy.FailureCode(operationInspection);
                var failure = diagnosticFailure is not null ? new CliError(diagnosticFailure,
                    "Diagnostic only: original outcome remains unresolved or evidence is incomplete. No replay, finalization or native action is authorized.")
                    : historicalShutdown ? new CliError("shutdown_already_consumed", "This is a prior shutdown receipt; no new shutdown was requested. Inspect daemon status before creating a separate stop intent.")
                    : logPage is { Available: false } ? new CliError(logPage.Reason ?? "log_source_unavailable", "The selected log source is unavailable; inspect data.reason. No workload or provider was started.")
                    : remote is { Available: false } ? new CliError(remote.Reason ?? "remote_unavailable", "Remote observation is unavailable; inspect data.reason and snapshot.error. No remote workload control was attempted.")
                    : startup is { Available: false } or { HasEntryErrors: true } or { SourceIssues.Count: > 0 } ? new CliError(startup.Reason ?? "startup_unavailable", "Startup inventory is unavailable or partial; inspect data. No startup item was changed or executed.")
                    : nativeInspection is { Slots.Archive.State: "unknown" } or { Slots.Original.State: "unknown" }
                        ? new CliError(nativeInspection.Slots.Archive.Reason == "archive_permission_denied" || nativeInspection.Slots.Original.Reason == "original_permission_denied"
                            ? "startup_native_permission_denied" : "startup_native_observation_unknown",
                            "Native slot observation is incomplete; inspect data.slots. No restore/rebuild eligibility or native write authority is established.")
                    : registration is { Available: false } ? new CliError(registration.Reason!, "Registration metadata is unavailable; no startup registration or task was changed or executed.")
                    : response.Ok && executionFailure ? new CliError("execution_failed", "The operation or one or more batch entries failed. Inspect data for individual results.")
                    : response.Ok ? null : new CliError(response.ErrorCode ?? "command_failed", response.Error ?? "Agent command failed.");
                var envelope = new CliEnvelope(1, response.Id, response.OperationId, response.Ok && failure is null, data, failure,
                    response.Revision ?? String(element, "revision") ?? String(element, "configurationRevision"),
                    nativeInspection?.CompletedAt ?? Timestamp(element, "observedAt") ?? Timestamp(element, "capturedAt") ?? Timestamp(element, "latestRecordAt"),
                    response.RootId, response.Replayed);
                if (envelope.Ok && parsed.Option("export") is { } destination)
                { await ExportLogsAsync(destination, paths, envelope, cancellationToken); }
                if (envelope.Ok && rpc == "configuration-document" && !json)
                {
                    var document = element.Deserialize<ConfigurationDocumentSnapshot>(AgentProtocol.JsonOptions)
                        ?? throw new JsonException("Invalid configuration document snapshot.");
                    if (!document.Utf8Valid) { throw new ArgumentException("Current YAML is not valid UTF-8; use --raw --json for its byte-preserving snapshot."); }
                    var bytes = new ConfigurationDocumentRequest(document.Utf8Base64Url).Decode();
                    if (ConfigurationDocumentRequest.Hash(bytes) != document.Sha256 || bytes.Length != document.ByteCount)
                    { throw new JsonException("Configuration document byte identity mismatch."); }
                    await error.WriteLineAsync($"revision: {document.Revision}");
                    await output.WriteAsync(new UTF8Encoding(false, true).GetString(bytes));
                }
                else { await CliOutput.WriteAsync(output, error, json, envelope); }
                if (!json && envelope.Ok && parsed.Route.Key is ("monitor status" or "monitor watch") && parsed.Option("task") is not null)
                { await output.WriteLineAsync("Local task name/identity view of the returned sample, not current disk configuration. Agent/host/group metadata remains global; empty rows are not proof of existence or health. Inspect availability/error and --json."); }
                if (!json && selfPreparationRequested)
                { await output.WriteLineAsync("Read-only point-in-time preparation, NOT confirmation/ownership/CAS. Save JSON.data (not the envelope); review before self set --file JSON --yes. The server revalidates everything."); }
                if (!json && rpc == "startup-self-set" && envelope.Ok)
                { await output.WriteLineAsync("Historical operation acknowledgement with opaque driver evidence, NOT proof of Windows logon launch. No target was launched. Query operation show with this ID; never blind-replay an unknown outcome."); }
                if (!envelope.Ok) { return ExitCode(envelope.Error!.Code); }
                var following = parsed.Route.Key == "logs" && parsed.Option("follow") is not null;
                if (parsed.Route.Key != "monitor watch" && !following || parsed.Option("count") is not null && index + 1 >= count) { return 0; }
                if (following) { request = request with { Payload = logQuery! with { Cursor = logPage!.NextCursor } }; }
                await output.FlushAsync(cancellationToken);
                if (!following || logPage?.HasMore != true)
                { await Task.Delay(TimeSpan.FromSeconds(int.Parse(parsed.Option("interval") ?? (following ? "1" : "10"), System.Globalization.CultureInfo.InvariantCulture)), cancellationToken); }
            }
        }
        catch (AgentOutcomeUnknownException exception)
        { return await Fail("outcome_unknown", exception.Message, exception.OperationId, exception.RequestId); }
        catch (AgentCommandException exception)
        { return await Fail(exception.Code, exception.Message, exception.OperationId ?? operationId, exception.RequestId, exception.Revision); }
        catch (CliAuthorizationException exception) { return await Fail("authorization_required", exception.Message); }
        catch (UnauthorizedAccessException exception) { if (selfMutationRequested) { operationId = null; } return await Fail("permission_denied", exception.Message); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (selfPreparationRequested) { return await Fail("cancelled", "Read-only self preparation was cancelled; no mutation was requested."); }
            if (selfMutationRequested) { operationId = null; return await Fail("cancelled", "Self request was cancelled before transport dispatch; no mutation was sent."); }
            if (inspectionRequested) { return await Fail("cancelled", "Read-only startup inspection was cancelled; no native write or execution was requested."); }
            if (streaming && operationId is null) { return 0; }
            return await Fail("cancelled", operationId is null
                ? "Command was cancelled before completion; no successful result is reported."
                : "Mutation was cancelled before confirmation; inspect its operation ID before retrying.");
        }
        catch (OperationCanceledException exception) { if (selfMutationRequested) { operationId = null; } return await Fail("timeout", exception.Message); }
        catch (TimeoutException exception) { if (selfMutationRequested) { operationId = null; } return await Fail("timeout", exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or ConfigurationException or DirectoryNotFoundException)
        { if (selfMutationRequested) { operationId = null; } return await Fail("invalid_argument", exception.Message); }
        catch (KeyNotFoundException exception) { return await Fail("not_found", exception.Message); }
        catch (IOException exception) { if (selfMutationRequested) { operationId = null; } return await Fail("connection_failed", exception.Message); }
        catch (InvalidOperationException exception) { return await Fail("command_failed", exception.Message); }
        catch (Exception exception) { return await Fail("internal_error", exception.Message); }

        async Task<int> Fail(string code, string message, string? id = null, string? requestId = null, string? revision = null)
        {
            await CliOutput.WriteAsync(output, error, json, new(1, requestId, id ?? operationId, false, null,
                new(code, message), revision, null, rootId));
            return ExitCode(code);
        }
    }

    // Fast read-only refusal for an already published hint. The submission path
    // still reserves atomically; this Missing observation is not dispatch authority.
    private static async Task RequireNoRecoveryPendingAsync(ProjectPaths paths, string name, CancellationToken token)
    {
        var store = new RecoveryPendingStore(paths, name); RecoveryPendingSnapshot pending;
        try { pending = await store.ReadAsync(token); }
        catch (OperationCanceledException)
        { throw new AgentCommandException("cancelled", "Recovery pending observation was cancelled before connection; no resume request was sent."); }
        if (!pending.BlocksSubmission) { return; }
        throw pending.State == RecoveryPendingState.Present
            ? new AgentCommandException("recovery_busy", $"This task retains an unresolved recovery operation in {store.FileName}. Only query operation show {pending.Operation!.OperationId}; history does not unlock recovery. Do not resubmit, replace its ID or clear the hint to retry.", operationId: pending.Operation.OperationId)
            : new AgentCommandException("pending_storage_unavailable", $"Recovery hint {store.FileName} cannot be verified. No resume request was sent; no operation ID or permission is inferred.");
    }

    private static AgentCommandException SelfPendingBlocked(SelfStartupPendingSnapshot pending) => pending.State == SelfStartupPendingState.Present
        ? new("self_busy", $"This root retains an unresolved self operation in {SelfStartupPendingStore.FileName}; it is not confirmation or admission proof. Only query operation show {pending.Operation!.OperationId}; do not resubmit, replace its ID or clear the hint to retry.", operationId: pending.Operation.OperationId)
        : new("pending_storage_unavailable", $"Recovery hint {SelfStartupPendingStore.FileName} cannot be verified. Self prepare/set remain blocked; no recovery ID or permission is inferred.");

    private static async Task<AgentResponse> SendSelfAsync(ProjectPaths paths, string rootId, CliRequest request,
        Func<ICliTransport> connect, TextWriter diagnostic, CancellationToken token)
    {
        var id = OperationJournal.NormalizeId(request.OperationId);
        var self = (SelfStartupMutationRequest)request.Payload!;
        var pending = new SelfStartupPendingStore(paths); SelfStartupPendingStore.Reservation reserved;
        try
        {
            var leaseTimeout = request.Timeout < TimeSpan.FromSeconds(15) ? request.Timeout : TimeSpan.FromSeconds(15);
            reserved = await pending.ReserveAsync(id, self.Enabled, token, leaseTimeout);
        }
        catch (Exception error)
        {
            // Publication can have succeeded even if no capability returned. Never
            // send/clear here or present the unreserved candidate as a recovery ID.
            SelfStartupPendingSnapshot observed;
            try { observed = await pending.ReadAsync(); }
            catch (Exception) { throw SelfPendingBlocked(new(SelfStartupPendingState.Unavailable)); }
            if (observed.BlocksSubmission) { throw SelfPendingBlocked(observed); }
            if (error is OperationCanceledException) { throw; }
            throw SelfPendingBlocked(new(SelfStartupPendingState.Unavailable));
        }
        ICliTransport? transport = null; var enteredExchange = false; AgentResponse response;
        try
        {
            if (reserved.Operation.RootId != rootId) { throw new ArgumentException("Self reservation and captured transport root differ."); }
            await diagnostic.WriteLineAsync($"reserved operationId: {id} (not admission proof; retain for original-ID recovery if dispatch becomes uncertain)");
            await diagnostic.FlushAsync(token);
            transport = connect(); enteredExchange = true;
            response = await transport.SendAsync(request, token);
            response = AgentSelfStartupClient.ReadAcknowledgement(response, id, rootId);
        }
        catch (Exception error)
        {
            // Only our concrete AgentClient transport distinguishes attemptedWrite.
            // A raw exception from an injected SendAsync is NOT pre-send evidence.
            if (!enteredExchange || transport is PipeTransport && error is not AgentOutcomeUnknownException
                && error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or ArgumentException)
            {
                await ReleaseSelfPendingAsync(reserved, null); throw;
            }
            var requestId = error is AgentOutcomeUnknownException unknown && unknown.RequestId is { Length: > 0 and <= AgentProtocol.MaximumRequestIdCharacters }
                ? unknown.RequestId : null;
            throw new AgentOutcomeUnknownException(id, error, requestId);
        }
        if (!response.Replayed && (response.Ok || response.ErrorCode == "not_executed"))
        { await ReleaseSelfPendingAsync(reserved, response); }
        else
        {
            try { await diagnostic.WriteLineAsync($"pending retained: {id} in {SelfStartupPendingStore.FileName}; this is not a fresh definitive outcome. Only inspect the original receipt; history/query does not unlock self writes."); }
            catch (Exception error) { throw new AgentOutcomeUnknownException(id, error, response.Id); }
        }
        return response;
    }
    private static async Task ReleaseSelfPendingAsync(SelfStartupPendingStore.Reservation reserved, AgentResponse? acknowledgement)
    {
        try { await reserved.ReleaseAfterKnownOutcomeAsync(CancellationToken.None); }
        catch (Exception)
        {
            throw new AgentCommandException("pending_clear_unverified", acknowledgement is null
                ? "This request was not dispatched, but recovery-hint clearance is unverified; self writes remain blocked. No retry or replacement ID is authorized."
                : "A fresh acknowledgement was received, but recovery-hint clearance is unverified; self writes remain blocked. Inspect the original receipt, not current registration or historical Data.",
                operationId: reserved.Operation.OperationId, requestId: acknowledgement?.Id);
        }
    }

    private static async Task ExportLogsAsync(string destination, ProjectPaths paths, CliEnvelope envelope, CancellationToken token)
    {
        var path = Core.Logging.LogSourcePolicy.ResolveFilePath(destination, Environment.CurrentDirectory);
        var protectedPaths = new[] { paths.ConfigPath, paths.OverridesPath, paths.GuiSettingsPath, paths.GuiCachePath,
            paths.StartupCachePath, paths.StartupBackupPath };
        if (protectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        { throw new ArgumentException("A log export cannot target a Vigil configuration/state file."); }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, AgentProtocol.JsonOptions) + "\n");
        if (bytes.Length > 128 * 1024) { throw new ArgumentException("Log export exceeds the bounded snapshot limit."); }
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
        }
        catch (IOException exception)
        { throw new AgentCommandException("export_failed", "Could not create a new local export; existing files are never overwritten. " + exception.Message); }
    }

    private static async Task<T> ReadInputWithTimeoutAsync<T>(Func<CancellationToken, Task<T>> read, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        // Console.In can block synchronously even in ReadAsync. Isolate that one local read;
        // a non-cooperative OS read may survive the wait, but can never reach transport dispatch.
        // In the Console entry point it is reclaimed when this invocation exits.
        var inputToken = deadline.Token;
        var pending = Task.Run(() => read(inputToken), inputToken);
        try { return await pending.WaitAsync(inputToken); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Input was not completed within --timeout; no request was sent. Close stdin after writing the complete payload."); }
        finally
        {
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task<ConfigurationDocumentRequest> ReadYamlInputAsync(string file, TextReader input, CancellationToken token)
    {
        if (file == "-") { return ConfigurationDocumentRequest.FromText(await ReadBoundedAsync(input, token)); }
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token);
                if (read == 0) { return ConfigurationDocumentRequest.FromBytes(bytes.ToArray()); }
                if (bytes.Length + read > ConfigurationDocumentRequest.MaximumBytes) { throw new ArgumentException("YAML input exceeds the 40 KiB document limit."); }
                bytes.Write(buffer, 0, read);
            }
        }
        catch (IOException exception) { throw new ArgumentException("Could not read input YAML: " + exception.Message, exception); }
    }

    private static async Task<string> ReadInputAsync(string file, TextReader input, CancellationToken token)
    {
        if (file == "-") { return await ReadBoundedAsync(input, token); }
        try
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read), new UTF8Encoding(false, true));
            return await ReadBoundedAsync(reader, token);
        }
        catch (IOException exception) { throw new ArgumentException("Could not read input JSON: " + exception.Message, exception); }
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken token)
    {
        var buffer = new char[4096]; var result = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) { return result.ToString(); }
            result.Append(buffer, 0, read);
            if (result.Length > AgentProtocol.MaximumRequestCharacters) { throw new ArgumentException("Input exceeds the bounded 64 Ki-character request size."); }
        }
    }

    private static JsonObject FilterMonitoringTask(object? data, string name, AgentResponse response)
    {
        try
        {
            // Project a detached JSON tree, not a DTO roundtrip: preserve unknown metadata
            // and row fields. This selects existing observations; it never resamples them.
            var snapshot = JsonNode.Parse(Element(data).GetRawText()) as JsonObject ?? throw new JsonException();
            if (snapshot["schemaVersion"]?.GetValue<int>() != 1) { throw new JsonException(); }
            var tasks = GroupRows("tasks"); var processes = GroupRows("processes");
            var runtime = snapshot["runtime"] as JsonArray ?? throw new JsonException();
            var selected = Matching(tasks, "name");
            if (selected.Length > 1) { throw new JsonException(); }
            if (selected.Length == 1 && (new[] { "name", "type", "executable", "arguments", "workingDirectory", "distro", "service", "command", "health", "launch" }
                .Any(key => !selected[0].AsObject().ContainsKey(key)) || selected[0]["launch"]?["runAs"] is null))
            { throw new JsonException(); } // Never invent identity-affecting defaults for a partial definition.
            if (selected.SingleOrDefault()?["health"] is { } health && (health is not JsonObject healthObject
                || new[] { "type", "pattern", "host", "port", "timeoutSeconds", "distro", "service" }.Any(key => !healthObject.ContainsKey(key))))
            { throw new JsonException(); }
            var definition = selected.SingleOrDefault()?.Deserialize<TaskSnapshot>(AgentProtocol.JsonOptions);
            if (definition is not null && (string.IsNullOrWhiteSpace(definition.Type) || definition.Arguments is null || definition.Launch is null))
            { throw new JsonException(); }
            var identity = definition is null ? null : MonitoringTaskIdentity.For(definition);
            snapshot["tasks"]!["data"] = tasks is null ? null : Copy(selected);
            snapshot["processes"]!["data"] = processes is null ? null : Copy(Matching(processes, "taskName").Where(SameIdentity));
            snapshot["runtime"] = Copy(Matching(runtime, "taskName").Where(SameIdentity));
            return snapshot;

            JsonArray? GroupRows(string key)
            {
                var group = snapshot[key] as JsonObject ?? throw new JsonException();
                var available = group["available"]?.GetValue<bool>() ?? throw new JsonException();
                if (!group.ContainsKey("data") || available && group["data"] is null) { throw new JsonException(); }
                return group["data"] is null ? null : group["data"] as JsonArray ?? throw new JsonException();
            }
            JsonNode[] Matching(JsonArray? rows, string key) => rows is null ? [] : rows.Where(row =>
            {
                if (row is not JsonObject || row[key] is not { } value || string.IsNullOrWhiteSpace(value.GetValue<string>())) { throw new JsonException(); }
                return string.Equals(value.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase);
            }).Select(row => row!).ToArray();
            bool SameIdentity(JsonNode row) => identity is not null && row["taskIdentity"]?.GetValue<string>() == identity;
            static JsonArray Copy(IEnumerable<JsonNode> rows) => new(rows.Select(row => row.DeepClone()).ToArray());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { throw new AgentCommandException("protocol_mismatch", "Cannot safely select a task from the returned monitoring snapshot; no unfiltered or healthy result was substituted.", response.Revision, requestId: response.Id); }
    }

    private static ExceptionLogSnapshot ReadExceptionLogs(object? data, string requestId)
    {
        try
        {
            var element = Element(data);
            if (element.ValueKind != JsonValueKind.Object || new[] { "capturedAt", "entries", "sourceCount", "truncated" }.Any(key => !element.TryGetProperty(key, out _)))
            { throw new JsonException("Missing legacy exception snapshot fields."); }
            var rows = element.GetProperty("entries");
            if (rows.ValueKind != JsonValueKind.Array || rows.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Object
                || new[] { "timestamp", "level", "source", "message", "fileName" }.Any(key => !row.TryGetProperty(key, out _))))
            { throw new JsonException("Missing legacy exception entry fields."); }
            var snapshot = element.Deserialize<ExceptionLogSnapshot>(AgentProtocol.JsonOptions);
            if (snapshot is null || snapshot.CapturedAt == default || snapshot.SourceCount < 0 || snapshot.Entries is null
                || snapshot.Entries.Count > 200 || snapshot.SourceCount == 0 && snapshot.Entries.Count != 0
                || snapshot.Entries.Any(entry => entry is null || entry.Timestamp == default || !Enum.IsDefined(entry.Level)
                    || entry.Source is null || entry.Message is null || entry.FileName is null))
            { throw new JsonException("Invalid legacy exception snapshot values."); }
            return snapshot;
        }
        catch (JsonException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned an incompatible legacy exception snapshot; no completeness or health conclusion is available.", requestId: requestId); }
    }

    private static JsonElement Element(object? data) => data is JsonElement element ? element : JsonSerializer.SerializeToElement(data, AgentProtocol.JsonOptions);
    private static string? String(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? Timestamp(JsonElement data, string property) => DateTimeOffset.TryParse(String(data, property),
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
    public static int ExitCode(string code) => code switch
    {
        "invalid_argument" or "invalid_request" or "invalid_operation_id" or "configuration_error" => 2,
        "connection_failed" or "protocol_mismatch" or "pending_storage_unavailable" => 3,
        "permission_denied" or "authorization_required" or "startup_preflight_permission_denied" or "startup_native_permission_denied" => 4,
        "revision_conflict" or "startup_entry_changed" or "startup_backup_changed" => 5,
        "startup_operation_unknown" or "startup_operation_busy" or "timeout" or "request_timeout" or "startup_native_observation_timeout" or "cancelled" or "outcome_unknown" or "operation_running" or "operation_in_progress" or "operation_result_unavailable" or "self_busy" or "recovery_busy" or "pending_clear_unverified" => 6,
        _ => 1,
    };

    public static string Version => typeof(CliApplication).Assembly.GetName().Version?.ToString() ?? "unknown";
    public const string HelpText = """
        vigil — shared Vigil control plane (Console; no WPF, no implicit daemon/UAC)
        Usage: vigil [--root DIR] COMMAND [NAME] [OPTIONS]
        Help: vigil help [COMMAND|skill] | vigil COMMAND --help (groups, commands, and built-in AI skill/schema).
              Help is local: no root/input access, Agent connection, daemon start or UAC.
          task list/show/start/stop/restart/start-missing
          task add --file JSON | remove/enable/disable/track/untrack NAME
          config show [--raw] | path
          config validate --file YAML | config apply --file YAML --expected-revision SHA256 --yes
          settings get/set/patch (--file JSON for set/patch)
          host list | show ID | add/update/upsert --file JSON | remove ID
          host status [ID] (no ID: local; ID: configured, enabled, read-only SSH target)
          monitor status [--task NAME] | monitor watch [--task NAME] [--interval SECONDS] [--count N]
            --task is an exact case-insensitive name, not a wildcard/substring; no implicit trim.
            Selects existing task rows and matching target identities in each returned sample, not current disk config.
            Agent/host/group availability/error/timestamps stay global and unchanged; empty is not health or existence proof.
            Unavailable cache remains diagnostic, not not_found; no new samples, daemon start, UAC or workload actions.
          monitor history NAME [--window 24h|7d|30d] [--buckets 1..1000]
          monitor history --file QUERY_JSON
          logs sources | path
          logs exceptions [--level warning|error|critical] [--search TEXT]
            Read-only legacy GUI aggregate; filters apply locally after the fixed snapshot cap.
            No --yes, payload, revision, operation ID, cursor/follow or export. No daemon start/UAC.
          logs [--task NAME | --source ID] [--level LEVEL] [--since ISO_WITH_OFFSET]
               [--tail 1..200] [--from-start | --cursor TOKEN] [--follow --interval SECONDS --count PAGES]
               [--export NEW_LOCAL_FILE]
          startup list [--scope all|user|machine] [--type all|registry|shortcut]
                       [--state all|active|disabled] [--search TEXT] [--limit 1..200]
          startup operation inspect UUIDv7 [--json]
            Read-only diagnostic via an already-running Agent. No daemon start/UAC/replay or native writes.
            0: complete diagnostic, NOT original success or authority; 6: unknown/busy; 1: missing/unverified/incomplete.
          startup inspect ENTRY_ID --file JSON --expected-revision SHA256 [--native]
            Default: schema 1 metadata review. --native: schema 2 read-only disabled-shortcut slot observations.
            Optional rebuildInput {target, arguments, workingDirectory}: lexical preview only, NOT native eligibility.
          startup adopt ENTRY_ID --file JSON --expected-revision SHA256 --yes
          startup self status | startup boot status (read-only global registration metadata)
          startup self prepare enable|disable [--json]
          startup self set --file JSON --yes [--operation-id UUIDv7]
          recovery state | resume NAME
          autorestart status | run RULE_ID --yes
          daemon status/start/stop | operation show ID | doctor
        Global: --json --timeout SECONDS --non-interactive --help --version
        Revision: --expected-revision SHA256 for configuration changes / recovery resume / startup inspect.
                 --operation-id UUIDv7 allows safe receipt lookup/retry with SAME parameters.
        Input: --file PATH or --file - (bounded UTF-8 stdin; YAML for config validate/apply, JSON otherwise).
               Full config apply, remove and resume require --yes. YAML exchange is limited to 40 KiB.
        config show --raw writes only verbatim UTF-8 YAML to stdout and revision to stderr; --json
        returns a byte-preserving base64url snapshot. validate is read-only and reports a known-field diff.
        apply preserves submitted UTF-8 bytes/BOM/comments/unknowns and separate overrides/preferences;
        current overrides can still determine enabled values. Backups use fixed ROOT/backups/config.
        Cooperating clients use revision CAS; external editors can race the final filesystem commit.
        Start daemon explicitly first. daemon stop stops monitoring, NOT managed workloads.
        JSON is one schemaVersion=1 envelope per line, including errors; diagnostics never pollute it.
        Exit: 0 success, 1 execution/partial failure, 2 input, 3 connection/protocol,
              4 permission/confirmation, 5 revision/selected-record conflict, 6 timeout/cancel/unknown.
        recovery state keeps Agent data/JSON unchanged. Local unresolved/unreadable hints for validated
        returned recovery task names are diagnosed on stderr, also with --json; they do not change the
        Agent exit status or unlock recovery. This is not an offline/orphan-hint inventory or admission proof.
        settings set uses on-disk snake_case keys; settings patch uses autoRecoverFailedTasks /
        autoStartMissingOnLaunch. Input templates match Core DTOs. Text output abbreviates fields;
        use --json for complete data. Ctrl+C ends watch/follow only, not the daemon or workloads.
        logs exceptions keeps the legacy GUI contract, NOT configured log_sources: root/log-directory
        top-level *.log* files, keyword severity heuristics, at most 32 files / 240 tail lines per file /
        200 entries. Each file uses a bounded 256 KiB tail window plus a small encoding probe; the first
        partial line is omitted. This is not a hard wall-time guarantee. sourceCount counts attempted files.
        Timestamps may fall back to file mtime. truncated reports byte/line/file/entry caps,
        not read failures or keyword-classification omissions;
        false/empty is NOT health, availability or completeness. --level/--search filter only the returned
        snapshot (same fields as GUI), never full logs; JSON keeps original sourceCount/truncated/capturedAt.
        Logs default to control:agent; --task selects that task's control file. Business files use
        configured log_sources IDs (settings set + workspace revision). --from-start starts bounded
        forward paging; --tail is also the page size for cursor/from-start reads. --export writes one bounded
        NDJSON envelope, never overwrites, and cannot be combined with --follow. Unknown timestamps
        are excluded with a visible count when --since is used. Unknown levels remain readable.
        WSL journal / Windows Event providers are explicitly unavailable; no wsl.exe is executed.
        Host add rejects existing IDs; update rejects missing IDs and replaces the complete definition.
        Remote status accepts only a configured ID, never an ad-hoc host/command. Unavailable source:
        exit 1, available=false, observedAt=null. SSH inputs require absolute local non-reparse files;
        SSH config aliases/proxies are not loaded. A disconnected CLI does not cancel the server early.
        Startup list is read-only native registry/shortcut metadata plus this root's disabled backups;
        active means a configured startup entry, not a running process. Limits/truncation and entry errors
        remain visible. ConfigurationRevision describes managed-task matching, NOT an atomic revision
        of global Windows startup state; do not use it to authorize a future startup mutation.
        Startup inspect JSON: {"source": <source>, "name": <name>, "isDisabled": <boolean>, "metadataDigest": <digest from startup list>}.
        It re-reads the fixed source and binds the selected ID, displayed metadata and configuration revision.
        The digest is NOT native/backup identity or a write token. Default inspection does not check original
        backup availability. Explicit --native observes current fixed archive/original name slots independently;
        present is only a bounded regular object, NOT an authenticated historical backup. Both missing still grants
        no write eligibility. Unknown slots retain data with exit 1 (permission: 4); timeout/cancel returns exit 6
        without evidence. The shared worker remains busy until actual completion; no queued retry or fallback.
        Native actions remain blocked. "reviewable" only permits entering the configuration-only adoption form,
        not a commit: adoption still needs its complete form, current revision and explicit confirmation.
        Inspection writes/executes nothing, takes no --yes/--operation-id and never launches daemon/UAC.
        Registration status reports fixed global metadata, not binary trust or actual Windows launch.
        Self root association is lexical only; boot uses a protected copy, not the current user root.
        Self enabled remains unknown; boot enabled is Task Scheduler's setting, not workload liveness.
        Self prepare is read-only, single-flight and point-in-time: no --yes/--file/--operation-id/revision.
        Save ONLY its JSON.data object, review enabled/context/fingerprint, then supply it to self set.
        Strict schema 1 JSON requires enabled, confirmed, targetContextId and expectedRegistration;
        unknown/duplicate/case-aliased/missing fields are rejected. File confirmation never substitutes
        for this invocation's --yes. Set never prepares/refreshes inputs, retries, starts daemon or UAC.
        Only the current server package's fixed GUI/current actor/root can qualify; no exe/key/SID inputs.
        Package-list integrity is not publisher certification. No target is launched; Boot writes stay closed.
        Self set durably reserves its operationId with the GUI's same-root recovery hint, then prints it on
        stderr BEFORE exchange (also with --json), not admission proof. Existing/unreadable hints block
        self prepare/set before connection; status and operation show stay read-only. Rejected input and
        proven pre-dispatch failures have no admitted ID; do not query a nonexistent receipt. Retain it: after
        timeout/disconnect/unknown query operation show ID; never substitute a new ID or fresh input for uncertainty.
        Only fresh success/not_executed or proven no-dispatch clears this invocation's exact hint. Replay,
        Completed/history, matching current state and queries never clear it; no force-clear/retry/UAC path.
        Set success DATA is opaque historical driver evidence, NOT current native state or launch proof;
        failure DATA is withheld: inspect the original operation receipt. Use --json for full acknowledgement.
        --timeout >0..600 seconds remains caller-selected; hashing can be expensive on cold/slow storage,
        and the default 30 seconds is NOT a latency guarantee. Cancel does not prove the server stopped.
        Startup adopt JSON: {"expectedEntry": <complete entry from startup list>, "task": <reviewed task-add form>}.
        It re-reads that metadata and writes ONLY this root's task configuration. Native changes return
        exit 5/startup_entry_changed; re-read and review, never auto-retry. It performs no native writes
        or execution calls; existing automatic policies can still run tasks. Native/other-root launchers
        remain independent: --yes acknowledges possible duplicate launch, not exclusive ownership.
        Pending parity (NOT claimed implemented): startup disable/restore/rebuild, boot install/uninstall,
        system log provider adapters and offline/orphan recovery hint inventory.
        GUI self, shared logs and remote RPC are wired; implementation coverage is not complete GUI/CLI acceptance.

        """ + CliHelp.SkillSummarySection;

    private sealed class PipeTransport(AgentClient client) : ICliTransport
    {
        public async Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        {
            try
            {
                return await client.SendResponseAsync(request.Command, request.Name, request.Payload, request.Timeout, cancellationToken,
                    request.Revision, request.OperationId, requireData: request.Command == "operation");
            }
            catch (JsonException) when (request.Command == "operation")
            { throw new AgentCommandException("protocol_mismatch", "Agent returned a malformed operation query envelope."); }
        }
    }
}
