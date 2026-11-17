using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Agent;

internal sealed class AgentCommandDispatcher
{
    private readonly TaskOrchestrator _orchestrator;
    private readonly HostStatusReader _hostStatusReader;
    private readonly ConfigurationWorkspace _workspace;
    private readonly OperationJournal _operations;
    private readonly AgentMonitor? _monitor;
    private readonly AgentTaskController _tasks;
    private readonly AgentLogService _logs;
    private readonly AgentRemoteHostService _remoteHosts;
    private readonly AgentRemoteHostMonitor? _remoteMonitor;
    private readonly AgentStartupService _startup;
    private readonly SystemAutorun.Core.Services.IStartupOperationInspectionReader _startupOperations;
    private readonly AgentStartupRegistrationService _startupRegistration;
    private readonly SystemAutorun.Windows.Startup.SelfStartupRegistrationService _selfRegistration;
    private readonly string _selfRootId;
    private int _selfPreparationActive;
    private long _selfPreparationFinishedAt = -1;
    private readonly Action<AgentRequest, Exception>? _errorObserver;

    internal AgentCommandDispatcher(
        TaskOrchestrator orchestrator,
        HostStatusReader hostStatusReader,
        ConfigurationWorkspace workspace,
        AgentMonitor? monitor = null,
        OperationJournal? operations = null,
        AgentTaskController? tasks = null,
        SystemAutorun.Core.Services.IRemoteHostReader? remoteReader = null,
        SystemAutorun.Core.Services.IStartupSnapshotReader? startupReader = null,
        SystemAutorun.Core.Services.IStartupRegistrationReader? registrationReader = null,
        Action<AgentRequest, Exception>? errorObserver = null,
        SystemAutorun.Core.Services.IStartupSlotEvidenceReader? startupSlotReader = null,
        SystemAutorun.Core.Services.IStartupOperationInspectionReader? startupOperationReader = null,
        SystemAutorun.Windows.Startup.SelfStartupRegistrationService? selfRegistration = null,
        AgentRemoteHostMonitor? remoteMonitor = null,
        TimeProvider? clock = null)
    {
        _errorObserver = errorObserver;
        _orchestrator = orchestrator;
        _hostStatusReader = hostStatusReader;
        _workspace = workspace;
        _operations = operations ?? new OperationJournal(workspace.Paths);
        _selfRootId = AgentProtocol.ForRoot(workspace.Paths.RootDirectory).RootId;
        if (AgentProtocol.ForRoot(_operations.Paths.RootDirectory).RootId != _selfRootId)
        { throw new ArgumentException("Dispatcher and Journal must share the host-resolved root.", nameof(operations)); }
        _selfRegistration = selfRegistration ?? new SystemAutorun.Windows.Startup.SelfStartupRegistrationService(_operations);
        if (_selfRegistration.JournalInstanceId != _operations.InstanceId)
        { throw new ArgumentException("Self service must share the exact Agent Journal instance.", nameof(selfRegistration)); }
        _startupOperations = startupOperationReader ?? new SystemAutorun.Windows.Startup.StartupOperationInspectionReader(workspace.Paths);
        _monitor = monitor;
        _remoteMonitor = remoteMonitor;
        _tasks = tasks ?? new AgentTaskController(workspace, orchestrator);
        _logs = new AgentLogService(workspace);
        _remoteHosts = new AgentRemoteHostService(workspace, remoteReader ?? new SshRemoteHostReader(), remoteMonitor, clock);
        _startup = new AgentStartupService(workspace,
            startupReader ?? new SystemAutorun.Windows.Startup.StartupSnapshotReader(workspace.Paths),
            startupSlotReader ?? new SystemAutorun.Windows.Startup.StartupSlotEvidenceReader(workspace.Paths));
        _startupRegistration = new AgentStartupRegistrationService(AgentProtocol.ForRoot(workspace.Paths.RootDirectory).RootDirectory,
            registrationReader ?? new SystemAutorun.Windows.Startup.StartupRegistrationReader());
        if (monitor is not null && monitor.GetSnapshot().InstanceId != _operations.InstanceId)
        {
            throw new ArgumentException("Monitoring and command dispatcher must share the same Agent instance.");
        }
    }

    internal async Task<AgentResponse> DispatchAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Self owns the context-aware admission/publication pipeline on this
            // SAME Journal. Never put its operation ID through an outer RunAsync.
            var response = request.Command.Equals(SystemAutorun.Windows.Startup.SelfStartupRegistrationService.Command, StringComparison.OrdinalIgnoreCase)
                ? await ExecuteSelfAsync(request, cancellationToken).ConfigureAwait(false)
                : AgentProtocol.IsMutation(request.Command)
                    ? await _operations.RunAsync(request, token => ExecuteCoreAsync(request, token), cancellationToken).ConfigureAwait(false)
                    : await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Ok && !response.Replayed && AgentProtocol.IsConfigurationMutation(request.Command))
            {
                _monitor?.InvalidateConfiguration();
                _remoteMonitor?.InvalidateConfiguration();
            }
            if (response.Ok && !response.Replayed && IsTaskLifecycleMutation(request.Command))
            {
                _monitor?.RequestImmediateHealthCheck();
            }
            return response;
        }
        catch (Exception exception)
        {
            return ErrorResponse(request, exception);
        }
    }

    private static bool IsTaskLifecycleMutation(string command) => command.ToLowerInvariant() is
        "start" or "stop" or "restart" or "start-missing" or "start-on-app-launch";

    private async Task<AgentResponse> ExecuteCoreAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var configurationWrite = AgentProtocol.IsConfigurationMutation(request.Command) ? _remoteHosts.BeginConfigurationWrite() : null;
            object data = request.Command.ToLowerInvariant() switch
            {
                "ping" => new { status = "ready", pid = Environment.ProcessId, instanceId = _operations.InstanceId },
                "operation" => await _operations.GetAsync(RequiredName(request), cancellationToken).ConfigureAwait(false)
                    ?? throw new AgentCommandException("operation_not_found", "Operation has not been recorded; this does not authorize replay with a new ID."),
                "configuration" or "settings-get" => await _workspace.ReadAsync(cancellationToken).ConfigureAwait(false),
                "configuration-document" => await _workspace.ReadDocumentAsync(cancellationToken).ConfigureAwait(false),
                "configuration-validate" => await _workspace.ValidateDocumentAsync(
                    RequiredPayload<ConfigurationDocumentRequest>(request), cancellationToken).ConfigureAwait(false),
                "configuration-apply" => await _workspace.ApplyDocumentAsync(
                    RequiredPayload<ConfigurationDocumentRequest>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "settings-set" => await _workspace.SaveSettingsAsync(
                    RequiredPayload<ApplicationSettings>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "settings-patch" => await _workspace.PatchSettingsAsync(
                    RequiredPayload<ApplicationSettingsPatch>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "remote-upsert" => await _workspace.UpsertRemoteHostAsync(
                    RequiredPayload<RemoteHostDefinition>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "remote-add" => await _workspace.AddRemoteHostAsync(
                    RequiredPayload<RemoteHostDefinition>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "remote-update" => await _workspace.UpdateRemoteHostAsync(
                    RequiredPayload<RemoteHostDefinition>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "remote-status" => request.Payload is null
                    ? await _remoteHosts.ReadAsync(RequiredName(request), cancellationToken).ConfigureAwait(false)
                    : throw new ArgumentException("Remote status accepts a configured host ID only; no payload or ad-hoc target."),
                "remote-remove" => await _workspace.RemoveRemoteHostAsync(
                    RequiredName(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "task-add" => await _workspace.AddTaskAsync(
                    RequiredPayload<AddTaskRequest>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "task-update" => await _workspace.UpdateTaskAsync(
                    RequiredPayload<UpdateTaskRequest>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "task-reorder" => await _workspace.ReorderTasksAsync(
                    RequiredPayload<ReorderTasksRequest>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "task-remove" => await _workspace.RemoveTaskAsync(
                    RequiredName(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "task-monitor" => await _workspace.SetExpectedRunningAsync(
                    RequiredName(request), RequiredFlag(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "task-automation" => await _workspace.SetAutomationAsync(
                    RequiredName(request), RequiredPayload<TaskAutomationPatch>(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "task-enable" => await _workspace.SetEnabledAsync(
                    RequiredName(request), RequiredFlag(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "monitoring-snapshot" => RequiredMonitor().GetSnapshot(),
                "monitoring-history" => await RequiredMonitor().QueryHistoryAsync(
                    RequiredPayload<MonitoringHistoryQuery>(request), cancellationToken).ConfigureAwait(false),
                "resource-history" => await RequiredMonitor().QueryResourcesAsync(
                    RequiredPayload<ResourceHistoryQuery>(request), cancellationToken).ConfigureAwait(false),
                "recent-resources-v2" => await RequiredMonitor().QueryRecentResourcesAsync(
                    RequiredPayload<ResourceHistoryQuery>(request), cancellationToken).ConfigureAwait(false),
                "list" => await _orchestrator.ListAsync(cancellationToken).ConfigureAwait(false),
                "status" => await _orchestrator.StatusAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "start" => await _tasks.StartAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "stop" => await _tasks.StopAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "restart" => await _tasks.RestartAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "start-missing" => await _tasks.StartMissingAsync(cancellationToken).ConfigureAwait(false),
                "start-on-app-launch" => await _tasks.StartOnAppLaunchAsync(cancellationToken).ConfigureAwait(false),
                "recovery-state" => await _tasks.ReadRecoveryAsync(cancellationToken).ConfigureAwait(false),
                "recovery-resume" => await _tasks.ResumeAsync(RequiredName(request), RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "host-status" => await _hostStatusReader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false),
                "process-monitor" => await _orchestrator.ManagedProcessesAsync(cancellationToken)
                    .ConfigureAwait(false),
                "startup-operation-inspect" => await _startupOperations.ReadAsync(RequiredOperationInspectionId(request), cancellationToken).ConfigureAwait(false),
                "startup-list" => await _startup.ReadAsync(RequiredStartupQuery(request), cancellationToken).ConfigureAwait(false),
                "startup-inspect" => await _startup.InspectAsync(RequiredName(request), RequiredStartupInspection(request),
                    RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "startup-inspect-native" => await _startup.InspectNativeAsync(RequiredName(request), RequiredStartupInspection(request),
                    RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "startup-adopt" => await _startup.AdoptAsync(RequiredName(request), RequiredStartupAdoption(request),
                    RequiredRevision(request), cancellationToken).ConfigureAwait(false),
                "startup-self-prepare" => await PrepareSelfAsync(request, cancellationToken).ConfigureAwait(false),
                "startup-self-status" or "startup-boot-status" => await _startupRegistration.ReadAsync(
                    RequiredRegistrationKind(request), cancellationToken).ConfigureAwait(false),
                "log-sources" => await _logs.SourcesAsync(cancellationToken).ConfigureAwait(false),
                "logs" => await _logs.ReadAsync(RequiredPayload<LogQuery>(request), cancellationToken).ConfigureAwait(false),
                "exception-logs" => await _orchestrator.ExceptionLogsAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
                "doctor" => await _orchestrator.DoctorAsync(cancellationToken).ConfigureAwait(false),
                "reload" => await _orchestrator.ListAsync(cancellationToken).ConfigureAwait(false),
                "shutdown" => new { shutdownRequested = true },
                _ => throw new AgentCommandException("unknown_command", $"Unknown command: {request.Command}"),
            };
            var validation = data as ConfigurationValidationResult;
            return new AgentResponse(request.Id, validation?.Valid != false, data,
                Error: validation is { Valid: false } ? validation.Error : null,
                ErrorCode: validation is { Valid: false } ? "configuration_error" : null,
                Revision: (data as IRevisionedResult)?.Revision, OperationId: request.OperationId);
        }
        catch (Exception exception)
        {
            return ErrorResponse(request, exception);
        }
    }

    private void RequireSelfTransport(AgentRequest request)
    {
        if (request.ProtocolVersion != AgentProtocol.Version || request.RootId is not null && request.RootId != _selfRootId
            || request.TimeoutMilliseconds is { } timeout && (timeout <= 0 || timeout > AgentProtocol.MaximumRequestTimeoutMilliseconds))
        { throw new AgentCommandException("invalid_argument", "Invalid self registration transport context."); }
    }

    private Task<AgentResponse> ExecuteSelfAsync(AgentRequest request, CancellationToken token)
    {
        RequireSelfTransport(request);
        // Pipe already validates its root/version/budget; repeat the relevant
        // guards for direct in-process dispatch BEFORE dropping transport-only fields.
        return _selfRegistration.ExecuteAsync(request with { RootId = null, TimeoutMilliseconds = null }, token);
    }

    private async Task<SystemAutorun.Core.Models.SelfStartupMutationRequest> PrepareSelfAsync(AgentRequest request, CancellationToken token)
    {
        RequireSelfTransport(request);
        if (request.Command != "startup-self-prepare" || request.Name is not ("enable" or "disable")
            || request.Payload is not null || request.ExpectedRevision is not null || request.OperationId is not null)
        { throw new AgentCommandException("invalid_argument", "Self preparation accepts only enable or disable; no payload, revision or mutation ID."); }
        // One dispatcher is bound to this Agent/root. Never queue expensive proofs
        // behind all Pipe slots or release this guard before actual work unwinds.
        if (Interlocked.CompareExchange(ref _selfPreparationActive, 1, 0) != 0) { throw SelfPreparationBusy(); }
        try
        {
            var last = Volatile.Read(ref _selfPreparationFinishedAt);
            if (last >= 0 && Environment.TickCount64 - last < 250) { throw SelfPreparationBusy(); }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(30)); // Also bounded when no wire timeout or client disconnect signal exists.
            try { return await _selfRegistration.PrepareAsync(request.Name == "enable", budget.Token).ConfigureAwait(false); }
            finally { Volatile.Write(ref _selfPreparationFinishedAt, Environment.TickCount64); }
        }
        finally { Volatile.Write(ref _selfPreparationActive, 0); }
    }

    private static AgentCommandException SelfPreparationBusy() => new("configuration_busy", "Self preparation is busy; no operation was admitted. Request a fresh preparation later.");

    private AgentResponse ErrorResponse(AgentRequest request, Exception exception)
    {
        // In-process diagnostics only; never serialize stacks/payloads onto the Pipe.
        // An observer cannot change the command outcome or trigger a retry.
        try { _errorObserver?.Invoke(request, exception); }
        catch { }
        var code = exception switch
        {
            ConfigurationConflictException => "revision_conflict",
            AgentCommandException command => command.Code,
            AgentOutcomeUnknownException or ConfigurationOutcomeUnknownException => "outcome_unknown",
            ConfigurationConfirmationRequiredException => "authorization_required",
            ConfigurationSourceBusyException => "configuration_busy",
            ConfigurationException => "configuration_error",
            UnauthorizedAccessException => "permission_denied",
            KeyNotFoundException => "not_found",
            ArgumentException or JsonException => "invalid_argument",
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            IOException => "io_error",
            _ => "command_failed",
        };
        return new AgentResponse(request.Id, false, Error: request.Command.Equals("startup-operation-inspect", StringComparison.OrdinalIgnoreCase)
                ? "Read-only operation diagnostic failed; inspect its error code. No native action is authorized." : exception.Message, ErrorCode: code,
            Revision: (exception as ConfigurationConflictException)?.ActualRevision,
            OperationId: (exception as AgentCommandException)?.OperationId
                ?? (exception as AgentOutcomeUnknownException)?.OperationId ?? request.OperationId);
    }

    private AgentMonitor RequiredMonitor() => _monitor
        ?? throw new AgentCommandException("monitoring_unavailable", "Independent monitoring is unavailable on this Agent.");

    private static string RequiredOperationInspectionId(AgentRequest request)
    {
        if (request.Payload is not null || request.OperationId is not null || request.ExpectedRevision is not null)
        { throw new ArgumentException("Operation inspection accepts only the original ID as name, never a payload, revision or mutation operationId."); }
        return OperationJournal.NormalizeId(RequiredName(request));
    }

    private static string RequiredName(AgentRequest request) =>
        string.IsNullOrWhiteSpace(request.Name)
            ? throw new ArgumentException($"Command '{request.Command}' requires a name.")
            : request.Name;

    private static string RequiredRevision(AgentRequest request) =>
        string.IsNullOrWhiteSpace(request.ExpectedRevision)
            ? throw new ArgumentException(request.Command.Equals("recovery-resume", StringComparison.OrdinalIgnoreCase)
                ? "Resume requires expectedRevision from recovery-state."
                : request.Command.Equals("startup-inspect", StringComparison.OrdinalIgnoreCase)
                      || request.Command.Equals("startup-inspect-native", StringComparison.OrdinalIgnoreCase)
                     ? "Inspection requires expectedRevision from startup list."
                     : "This mutation requires expectedRevision from configuration/settings-get.")
            : request.ExpectedRevision;

    private static T RequiredPayload<T>(AgentRequest request) =>
        request.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object
            ? throw new ArgumentException($"Command '{request.Command}' requires an object payload.")
            : payload.Deserialize<T>(AgentProtocol.JsonOptions)
                ?? throw new ArgumentException("Payload must not be null.");

    private static string RequiredRegistrationKind(AgentRequest request)
    {
        if (request.Name is not null || request.Payload is not null || request.ExpectedRevision is not null || request.OperationId is not null)
        { throw new ArgumentException("Registration status accepts no name, payload, revision or mutation ID."); }
        return request.Command.Equals("startup-self-status", StringComparison.OrdinalIgnoreCase) ? "self" : "boot";
    }

    private static StartupInspectionRequest RequiredStartupInspection(AgentRequest request)
    {
        if (request.OperationId is not null || request.Payload is not JsonElement { ValueKind: JsonValueKind.Object } payload)
        { throw new ArgumentException("Startup inspection is read-only and requires a metadata reference, not a mutation ID."); }
        var strict = new JsonSerializerOptions(AgentProtocol.JsonOptions)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        return payload.Deserialize<StartupInspectionRequest>(strict) ?? throw new ArgumentException("Inspection cannot be null.");
    }

    private static StartupAdoptionRequest RequiredStartupAdoption(AgentRequest request)
    {
        if (request.Payload is not JsonElement { ValueKind: JsonValueKind.Object } payload)
        { throw new ArgumentException("Startup adoption requires a reviewed observation and task object."); }
        var strict = new JsonSerializerOptions(AgentProtocol.JsonOptions)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        return payload.Deserialize<StartupAdoptionRequest>(strict) ?? throw new ArgumentException("Adoption cannot be null.");
    }

    private static StartupListQuery RequiredStartupQuery(AgentRequest request)
    {
        if (request.Name is not null || request.Payload is not JsonElement { ValueKind: JsonValueKind.Object } payload)
        { throw new ArgumentException("Startup list accepts a filter object only, not a name, path or command."); }
        var strict = new JsonSerializerOptions(AgentProtocol.JsonOptions)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        return (payload.Deserialize<StartupListQuery>(strict) ?? throw new ArgumentException("Startup query cannot be null.")).Validate();
    }

    private static bool RequiredFlag(AgentRequest request) =>
        request.Payload is JsonElement { ValueKind: JsonValueKind.Object } payload
        && payload.TryGetProperty("enabled", out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException("Payload requires a boolean enabled field.");
}
