using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Cli;

public sealed record CliError(string Code, string Message);
public sealed record CliEnvelope(int SchemaVersion, string? RequestId, string? OperationId, bool Ok, object? Data,
    CliError? Error, string? Revision, DateTimeOffset? ObservedAt, string? RootId, bool Replayed = false);

public static class CliOutput
{
    public static async Task WriteAsync(TextWriter output, TextWriter error, bool json, CliEnvelope envelope)
    {
        if (json) { await output.WriteLineAsync(JsonSerializer.Serialize(envelope, AgentProtocol.JsonOptions)); return; }
        if (!envelope.Ok)
        {
            await error.WriteLineAsync($"{Safe(envelope.Error?.Code ?? "failed")}: {Safe(envelope.Error?.Message ?? "Command failed.")}");
            if (envelope.OperationId is not null) { await error.WriteLineAsync($"operationId: {Safe(envelope.OperationId)} (query operation show; do not retry with a new ID)"); }
            if (envelope.Revision is not null) { await error.WriteLineAsync($"revision: {envelope.Revision}"); }
            if (envelope.Data is null) { return; }
        }
        if (envelope.Revision is not null) { await output.WriteLineAsync($"revision: {envelope.Revision}"); }
        if (envelope.OperationId is not null) { await output.WriteLineAsync($"operationId: {envelope.OperationId}{(envelope.Replayed ? " (replayed)" : "")}"); }
        if (envelope.Ok && envelope.Data is Core.Models.ExceptionLogSnapshot exceptions)
        {
            await output.WriteLineAsync($"legacy exceptions | captured: {exceptions.CapturedAt:O} | source files: {exceptions.SourceCount} | entries: {exceptions.Entries.Count} | truncated: {exceptions.Truncated}");
            foreach (var entry in exceptions.Entries)
            { await output.WriteLineAsync($"[{entry.Timestamp:O}] {entry.Level} | {SafeException(entry.Source)} | {SafeException(entry.FileName)} | {SafeException(entry.Message, 1000)}"); }
            await output.WriteLineAsync("Legacy heuristic aggregate, not a health or source-availability check. Source files counts attempted files; read failures may be omitted. Empty or truncated=False does not prove complete or clean logs.");
            await output.WriteLineAsync("Retains up to 200 entries / 32 files / 240 tail lines per file within a 256 KiB tail window plus an encoding probe. Partial boundary lines and unclassified records are omitted; timestamps may fall back to file mtime. Truncated reports byte/line/file/entry caps, not read failures or every omission.");
            await output.WriteLineAsync("Filters apply only to this returned snapshot, not full logs. This is not configured log_sources paging; use logs sources / logs for that contract. Use --json for full retained messages.");
            return;
        }
        var data = JsonSerializer.SerializeToElement(envelope.Data, AgentProtocol.JsonOptions);
        var validation = ReadConfigurationValidation(data);
        if (validation is not null && (envelope.Ok || !validation.Valid && envelope.Error?.Code == "configuration_error"))
        { await WriteConfigurationValidationAsync(output, validation, data.GetProperty("scope").GetString()!); return; }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("diagnosticOnly", out _)
            && data.TryGetProperty("queriedOperationId", out _))
        {
            var diagnostic = data.Deserialize<Core.Models.StartupOperationInspectionSnapshot>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"operation diagnostic: {Safe(diagnostic.QueriedOperationId)} | journal: {Safe(diagnostic.JournalState)} | reason: {Safe(diagnostic.JournalReason)}");
            await output.WriteLineAsync($"receipt on disk: {Safe(diagnostic.PersistedReceiptState)} | conservative observation: {Safe(diagnostic.ObservedReceiptState)}");
            if (diagnostic.Evidence is { } evidence)
            {
                await output.WriteLineAsync($"history: {Safe(evidence.State)} ({Safe(evidence.Reason)}) | association: {Safe(evidence.AssociationState)} ({Safe(evidence.AssociationReason)})");
                await output.WriteLineAsync($"native: {Safe(evidence.NativeState)} ({Safe(evidence.NativeReason)}) | record: {Safe(evidence.RecordId ?? "--")} | transaction: {Safe(evidence.TransactionId ?? "--")}");
            }
            await output.WriteLineAsync("Diagnostic only, sequential observations: not original success, a global atomic snapshot, or authority to replay/finalize/restore/rebuild. Unknown does not prove the owner stopped. No native write, execution or receipt update requested.");
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("source", out _)
            && data.TryGetProperty("entries", out _) && data.TryGetProperty("scannedBytes", out _))
        {
            var page = data.Deserialize<Core.Models.LogPage>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"source: {Safe(page.Source.Id)} | available: {page.Available} | reason: {Safe(page.Reason ?? "--")} | truncated: {page.Truncated} | read: {page.ScannedBytes} bytes");
            if (page.UnknownTimestampExcluded > 0) { await output.WriteLineAsync($"unknown timestamp excluded by --since: {page.UnknownTimestampExcluded}"); }
            foreach (var entry in page.Entries)
            {
                var message = new string(entry.Message.Select(character => char.IsControl(character) && character is not ('\n' or '\t') ? ' ' : character).ToArray());
                await output.WriteLineAsync($"[{entry.Timestamp?.ToString("O") ?? "--"}] {entry.Level ?? "unknown"}{(entry.Truncated ? " [truncated]" : "")}{(entry.LossyUtf8 ? " [lossy UTF-8]" : "")} {message}");
            }
            if (page.NextCursor is not null) { await output.WriteLineAsync($"cursor: {page.NextCursor}"); }
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var adoption)
            && adoption.ValueKind == JsonValueKind.Object && adoption.TryGetProperty("nativeWritePerformed", out _))
        {
            await output.WriteLineAsync($"adopted entry: {Safe(adoption.GetProperty("entryId").GetString() ?? "--")} | task: {Safe(adoption.GetProperty("configuration").GetProperty("taskName").GetString() ?? "--")}");
            await output.WriteLineAsync("This operation made no native startup writes or execution calls. Existing native/other-root launchers and automatic policies remain independent; duplicate launch is possible.");
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("slots", out _)
            && data.TryGetProperty("historicalIdentity", out _))
        {
            var inspection = data.Deserialize<Core.Models.StartupNativeInspectionSnapshot>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"startup inspection: native-read-only | source: {Safe(inspection.Metadata.Entry.Source)} | name: {Safe(inspection.Metadata.Entry.Name)} | id: {Safe(inspection.Metadata.Entry.Id)}");
            await output.WriteLineAsync($"validation: {inspection.ValidationScope} | historical identity: {inspection.HistoricalIdentity}");
            var archive = inspection.Slots.Archive; var original = inspection.Slots.Original;
            await output.WriteLineAsync($"archive: {archive.State} | reason: {archive.Reason ?? "--"} | native error: {archive.NativeErrorCode?.ToString() ?? "--"} | case: {archive.CaseSensitivity} | observed: {archive.ObservedAt:O}");
            await output.WriteLineAsync($"original: {original.State} | reason: {original.Reason ?? "--"} | native error: {original.NativeErrorCode?.ToString() ?? "--"} | case: {original.CaseSensitivity} | observed: {original.ObservedAt:O}");
            await WriteRebuildPreviewAsync(output, inspection.Metadata.RebuildPreview);
            await output.WriteLineAsync("Independent point-in-time observations, NOT a snapshot/CAS or authenticated history. Present means a bounded regular object at the current fixed name, not the historical original.");
            await output.WriteLineAsync("No restore/rebuild eligibility, native write authority or execution. Both missing does not authorize reconstruction. Use --json for complete identities, metadata and blocked actions.");
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("originalBackupState", out _)
            && data.TryGetProperty("metadataDigest", out _))
        {
            var inspection = data.Deserialize<Core.Models.StartupInspectionSnapshot>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"startup inspection: metadata-only | source: {Safe(inspection.Entry.Source)} | name: {Safe(inspection.Entry.Name)} | id: {Safe(inspection.Entry.Id)}");
            await output.WriteLineAsync($"metadata digest: {inspection.MetadataDigest} | original backup: {inspection.OriginalBackupState}");
            foreach (var action in inspection.Actions)
            { await output.WriteLineAsync($"{action.Action}: {action.State} | reason: {action.Reason ?? "--"} | requires admin: {action.RequiresAdministrator}"); }
            await WriteRebuildPreviewAsync(output, inspection.RebuildPreview);
            await output.WriteLineAsync("Read-only metadata review; no native write authority, original-backup/target availability check or execution. Reviewable does not authorize a commit. Use --json for complete metadata.");
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("registrationId", out _) && data.TryGetProperty("rootAssociation", out _))
        {
            var status = data.Deserialize<Core.Models.StartupRegistrationSnapshot>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"{status.Kind} registration | scope: {status.RegistrationScope} | available: {status.Available} | reason: {Safe(status.Reason ?? "--")}");
            await output.WriteLineAsync($"registered: {status.Registration?.Registered?.ToString() ?? "unknown"} | enabled setting: {status.Registration?.Enabled?.ToString() ?? "unknown"} | state: {status.Registration?.State ?? "--"}");
            await output.WriteLineAsync($"root association: {status.RootAssociation} | associated root: {Safe(status.AssociatedRoot ?? "--")}");
            await output.WriteLineAsync("Global metadata only; not binary trust, an ownership token, workload liveness or proof Windows will launch it. Use --json for complete metadata.");
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("scannedEntries", out _)
            && data.TryGetProperty("entries", out _))
        {
            var inventory = data.Deserialize<Core.Models.StartupInventorySnapshot>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"startup available: {inventory.Available} | reason: {Safe(inventory.Reason ?? "--")} | scanned (observed lower bound): {inventory.ScannedEntries?.ToString() ?? "--"} | matched (observed lower bound): {inventory.MatchedEntries?.ToString() ?? "--"} | truncated: {inventory.Truncated}");
            foreach (var issue in inventory.SourceIssues)
            { await output.WriteLineAsync($"source unavailable: {Safe(issue.Source)} | {Safe(issue.Reason)}"); }
            await output.WriteLineAsync("source\tname\tstate\tmanaged task\trequires admin\torigin\tid\tread error");
            foreach (var entry in inventory.Entries)
            {
                await output.WriteLineAsync(string.Join('\t', new[] { entry.Source, entry.Name,
                    entry.IsDisabled ? "disabled-backup" : "configured", entry.ManagedTask ?? "--",
                    entry.RequiresAdministrator.ToString(), entry.Origin, entry.Id, entry.Error ?? "--" }.Select(value => Safe(value))));
            }
            if (inventory.Available && inventory.Entries.Count == 0) { await output.WriteLineAsync("(no entries in this bounded/filter result)"); }
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            await output.WriteLineAsync("source\tkind\torigin\ttask\tunavailable reason\tpath");
            foreach (var source in sources.EnumerateArray())
            {
                await output.WriteLineAsync(string.Join('\t', new[] { "id", "kind", "origin", "taskName", "unavailableReason", "path" }
                    .Select(key => source.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? Safe(value.ToString()) : "--")));
            }
            if (data.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            { await output.WriteLineAsync("Source list truncated; select a task or configured source directly."); }
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("maintenanceTaskInstalled", out _)
            && data.TryGetProperty("rules", out _))
        {
            var status = data.Deserialize<Core.Models.AutoRestartStatusSnapshot>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"auto-restart enabled: {status.Enabled} | agent elevated: {status.IsAgentElevated} | maintenance channel: {status.MaintenanceTaskInstalled}");
            if (!string.IsNullOrWhiteSpace(status.MaintenanceTaskError))
            {
                await output.WriteLineAsync($"maintenance warning: {Safe(status.MaintenanceTaskError)}");
            }
            await output.WriteLineAsync("id\tname\tenabled\tmatched\tcpu%\tmem_mb\thandles\tlast_result\tnext_scheduled");
            foreach (var rule in status.Rules)
            {
                var cpu = rule.LiveCpuPercent is { } c ? $"{c:0.0}" : "--";
                var mem = rule.LiveWorkingSetBytes is { } b ? $"{b / (1024.0 * 1024.0):0}" : "--";
                var handles = rule.LiveHandleCount is { } h ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) : "--";
                var last = rule.LastTriggeredAt is { } t ? $"{(rule.LastSucceeded == true ? "OK" : "FAIL")} ({t:O})" : "--";
                var next = rule.NextScheduledAt is { } n ? n.ToString("O") : "--";
                await output.WriteLineAsync(string.Join('\t', new[] { rule.RuleId, rule.Name, rule.Enabled.ToString(), rule.MatchedProcessCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cpu, mem, handles, last, next }.Select(Safe)));
            }
            if (status.Rules.Count == 0) { await output.WriteLineAsync("(no auto-restart rules configured)"); }
            return;
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("ruleId", out _)
            && data.TryGetProperty("affectedProcessCount", out _))
        {
            var run = data.Deserialize<Core.Models.AutoRestartRunResult>(AgentProtocol.JsonOptions)!;
            await output.WriteLineAsync($"autorestart run | rule: {Safe(run.RuleId)} | ok: {run.Succeeded} | affected: {run.AffectedProcessCount} | elevated: {run.UsedElevation} | {Safe(run.Message)}");
            return;
        }
        if (data.ValueKind == JsonValueKind.Array)
        {
            var rows = data.EnumerateArray().ToArray();
            if (rows.Length == 0) { await output.WriteLineAsync("(empty)"); return; }
            if (rows.All(row => row.ValueKind == JsonValueKind.Object))
            {
                var keys = new[] { "name", "taskName", "status", "state", "type", "succeeded", "message", "enabled", "healthy", "id" }
                    .Where(key => rows.Any(row => row.TryGetProperty(key, out _))).Take(8).ToArray();
                if (keys.Length > 0)
                {
                    await output.WriteLineAsync(string.Join("\t", keys));
                    foreach (var row in rows) { await output.WriteLineAsync(string.Join("\t", keys.Select(key => row.TryGetProperty(key, out var value) ? Safe(value.ToString()) : "--"))); }
                    return;
                }
            }
        }
        if (data.ValueKind == JsonValueKind.Object)
        {
            var history = envelope.Ok ? ReadMonitoring<Core.Models.MonitoringHistoryResult>(data, "taskName", "taskIdentity", "from", "to", "latestRecordAt", "retainedFrom", "healthySeconds", "faultSeconds", "unknownSeconds", "availabilityPercent", "coveragePercent", "intervals", "buckets", "truncated", "warning") : null;
            if (history is { SchemaVersion: 1 }) { await WriteMonitoringHistoryAsync(output, history); return; }
            var monitoring = envelope.Ok ? ReadMonitoring<Core.Models.AgentMonitoringSnapshot>(data, "heartbeatAt", "returnedAt", "tasks", "processes", "runtime") : null;
            foreach (var property in data.EnumerateObject())
            {
                if (property.Name == "runtime" && monitoring is { SchemaVersion: 1, Runtime: not null, Tasks: not null, Processes: not null })
                { await WriteMonitoringRuntimeAsync(output, monitoring); continue; }
                // Nested groups retain explicit Available/error/observation metadata; never
                // flatten stale cached payloads into a deceptively current table.
                await output.WriteLineAsync($"{property.Name}: {Safe(property.Value.ToString(), 2000)}");
            }
        }
        else { await output.WriteLineAsync(Safe(data.ToString(), 2000)); }
    }

    private static Core.Configuration.ConfigurationValidationResult? ReadConfigurationValidation(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || new[] { "schemaVersion", "valid", "revision", "capturedAt", "sha256", "byteCount", "taskCount", "changes", "error", "scope" }
            .Any(key => !data.TryGetProperty(key, out _)) || data.GetProperty("scope").ValueKind != JsonValueKind.String) { return null; }
        var changes = data.GetProperty("changes");
        if (changes.ValueKind != JsonValueKind.Null && (changes.ValueKind != JsonValueKind.Object
            || new[] { "available", "addedTasks", "removedTasks", "changedTasks", "settingsChanged" }.Any(key => !changes.TryGetProperty(key, out _)))) { return null; }
        try
        {
            var result = data.Deserialize<Core.Configuration.ConfigurationValidationResult>(AgentProtocol.JsonOptions);
            if (result is not { SchemaVersion: 1, Revision: not null, Sha256: not null, ByteCount: >= 0, TaskCount: >= 0 }) { return null; }
            if (result.Changes is { } diff && (diff.AddedTasks is null || diff.RemovedTasks is null || diff.ChangedTasks is null
                || diff.AddedTasks.Concat(diff.RemovedTasks).Concat(diff.ChangedTasks).Any(string.IsNullOrWhiteSpace))) { return null; }
            return result;
        }
        catch (JsonException) { return null; } // Presentation only: retain raw output for unknown/partial shapes.
    }

    private static async Task WriteConfigurationValidationAsync(TextWriter output, Core.Configuration.ConfigurationValidationResult validation, string scope)
    {
        await output.WriteLineAsync($"configuration validation: {(validation.Valid ? "valid" : "invalid")} | capturedAt: {validation.CapturedAt:O}");
        await output.WriteLineAsync($"current revision: {SafeException(validation.Revision)}");
        await output.WriteLineAsync($"candidate sha256: {SafeException(validation.Sha256)} | bytes: {validation.ByteCount} | tasks: {(validation.Valid ? validation.TaskCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown (candidate invalid)")}");
        await output.WriteLineAsync($"scope: {SafeException(scope, 1000)}");
        if (validation.Error is not null) { await output.WriteLineAsync($"validation error: {SafeException(validation.Error, 1000)}"); }
        if (!validation.Valid || validation.Changes is not { Available: true } diff)
        {
            await output.WriteLineAsync($"known-field diff: unavailable | reason: {SafeException(validation.Changes?.Reason ?? (validation.Valid ? "no comparison returned" : "candidate invalid; no usable comparison"), 1000)}");
        }
        else
        {
            await output.WriteLineAsync("known-field diff: available (Agent-reported task names, not individual changed properties)");
            await Rows("added", "+", diff.AddedTasks); await Rows("removed", "-", diff.RemovedTasks); await Rows("changed", "~", diff.ChangedTasks);
            await output.WriteLineAsync($"settings changed: {diff.SettingsChanged}");
            if (diff.Reason is not null) { await output.WriteLineAsync($"comparison reason: {SafeException(diff.Reason, 1000)}"); }
            if (diff.AddedTasks.Count + diff.RemovedTasks.Count + diff.ChangedTasks.Count == 0 && !diff.SettingsChanged)
            { await output.WriteLineAsync("no listed known-field changes; this does not compare the original document bytes"); }
        }
        await output.WriteLineAsync("Known-field summary, not a byte/text diff. Validation is point-in-time, not a reservation; it does not apply the candidate, test targets or run workloads. Review/revalidate before a separate confirmed apply. Use --json for the full original result.");

        async Task Rows(string category, string marker, IReadOnlyList<string> names)
        { await output.WriteLineAsync($"{category} tasks: {names.Count}"); foreach (var name in names) { await output.WriteLineAsync($"  {marker} {SafeException(name)}"); } }
    }

    private static T? ReadMonitoring<T>(JsonElement data, params string[] required) where T : class
    {
        if (required.Any(key => !data.TryGetProperty(key, out _))) { return null; }
        // Presentation only: unknown/older shapes retain the existing raw-metadata output.
        // JSON returns before any of these conversions; no wire contract or error policy changes.
        try { return data.Deserialize<T>(AgentProtocol.JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static async Task WriteMonitoringRuntimeAsync(TextWriter output, Core.Models.AgentMonitoringSnapshot snapshot)
    {
        if (snapshot.Runtime.Count == 0) { await output.WriteLineAsync("runtime: no current rows in returned snapshot; inspect group availability/error."); }
        foreach (var row in snapshot.Runtime)
        {
            if (row is null) { await output.WriteLineAsync("runtime: unknown row; use --json for original metadata."); continue; }
            var task = snapshot.Tasks.Data?.FirstOrDefault(task => string.Equals(task?.Name, row.TaskName, StringComparison.OrdinalIgnoreCase));
            var current = task is not null && snapshot.Tasks.Available && snapshot.Tasks.ObservedAt is { } tasksAt
                && tasksAt != default && tasksAt <= row.ObservedAt && row.ObservedAt <= snapshot.ReturnedAt && row.ObservedAt != default;
            var process = current && task!.Type == "windows" && snapshot.Processes.Available
                && snapshot.Processes.ObservedAt is { } processesAt && processesAt != default && processesAt <= row.ObservedAt
                && row.ProcessUptimeSource == "windows-process-observed-instances" && !string.IsNullOrWhiteSpace(row.ProcessIdentity);
            var healthy = current && task is { Enabled: true, Healthy: true, HealthObserved: true }
                && row.ConsecutiveHealthSource == "consecutive-passive-health-observations";
            await output.WriteLineAsync($"runtime: {SafeException(row.TaskName ?? "unknown")} | identity: {SafeException(row.TaskIdentity ?? "unknown")} | observedAt: {row.ObservedAt:O}");
            await output.WriteLineAsync($"  process-uptime: {ObservedDuration(row.ProcessStartedAt, row.ObservedAt, process)} | continuous-health: {ObservedDuration(row.ConsecutiveHealthySince, row.ObservedAt, healthy)}");
            await output.WriteLineAsync($"  process source: {SafeException(row.ProcessUptimeSource ?? "unknown")} | started: {row.ProcessStartedAt?.ToString("O") ?? "unknown"} | process identity: {SafeException(row.ProcessIdentity ?? "unknown")}");
            await output.WriteLineAsync($"  health source: {SafeException(row.ConsecutiveHealthSource ?? "unknown")} | since: {row.ConsecutiveHealthySince?.ToString("O") ?? "unknown"}");
        }
        await output.WriteLineAsync("Durations end at recorded observedAt: no extrapolation to now. Matched Windows processes use the earliest observed instance start, not host boot; continuous passive health is not service uptime. Unknown is not zero. Group data may be diagnostic cache when unavailable; use --json for complete metadata.");
    }

    private static string ObservedDuration(DateTimeOffset? since, DateTimeOffset at, bool available) =>
        available && since is { } start && start != default && at >= start
            ? (at - start).ToString("c", System.Globalization.CultureInfo.InvariantCulture) : "unknown";

    private static async Task WriteMonitoringHistoryAsync(TextWriter output, Core.Models.MonitoringHistoryResult history)
    {
        await output.WriteLineAsync($"observation history: {SafeException(history.TaskName ?? "unknown")} | identity: {SafeException(history.TaskIdentity ?? "unknown")}");
        await output.WriteLineAsync($"window: {history.From:O} .. {history.To:O} | latestRecordAt: {history.LatestRecordAt?.ToString("O") ?? "unknown"} | retainedFrom: {history.RetainedFrom?.ToString("O") ?? "unknown"}");
        await output.WriteLineAsync($"observed-availability: {MonitoringPercent(history.AvailabilityPercent)} | coverage: {MonitoringPercent(history.CoveragePercent)}");
        await output.WriteLineAsync($"healthy: {MonitoringSeconds(history.HealthySeconds)} | fault: {MonitoringSeconds(history.FaultSeconds)} | unknown: {MonitoringSeconds(history.UnknownSeconds)} | truncated: {history.Truncated}");
        await output.WriteLineAsync($"warning: {SafeException(history.Warning ?? "none")} | intervals: {history.Intervals?.Count.ToString() ?? "unknown"} | buckets: {history.Buckets?.Count.ToString() ?? "unknown"} (use --json for complete intervals/buckets)");
        await output.WriteLineAsync("Availability is healthy/(healthy+fault) within observed time; coverage is known time/window. Values are supplied by Agent, not recomputed here. Unknown is not healthy or zero; NOT precise SLA and not service uptime.");
    }

    private static string MonitoringPercent(double? value) => value is { } number && double.IsFinite(number) && number is >= 0 and <= 100
        ? number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%" : "unknown";
    private static string MonitoringSeconds(double value) => double.IsFinite(value) && value >= 0
        ? value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s" : "unknown";

    private static async Task WriteRebuildPreviewAsync(TextWriter output, Core.Models.StartupRebuildPreview? preview)
    {
        if (preview is null) { return; }
        await output.WriteLineAsync($"rebuild input preview: {preview.ValidationScope} | target availability: {preview.TargetAvailability} | input digest: {preview.InputDigest}");
        await output.WriteLineAsync($"target: {Safe(preview.Input.Target)} | args: {Safe(preview.Input.Arguments ?? "")} | working directory: {Safe(preview.Input.WorkingDirectory ?? "--")}");
        await output.WriteLineAsync("Proposed inputs only, not original backup data, eligibility or confirmation. Native rebuild remains blocked.");
    }

    private static string SafeException(string text, int limit = 160) => Safe(new string(text.Take(limit + 1)
        .Select(value => char.IsControl(value) || char.GetUnicodeCategory(value) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator ? ' ' : value).ToArray()), limit);

    private static string Safe(string text, int limit = 160) =>
        new string(text.Take(limit).Select(value => char.IsControl(value) ? ' ' : value).ToArray()) + (text.Length > limit ? "… (use --json for full data)" : "");

    public static bool HasExecutionFailure(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.Array => data.EnumerateArray().Any(HasExecutionFailure),
        JsonValueKind.Object => data.TryGetProperty("succeeded", out var succeeded) && succeeded.ValueKind == JsonValueKind.False
            || data.TryGetProperty("value", out var value) && HasExecutionFailure(value),
        _ => false,
    };
}
