using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Ipc;

// Durable at-most-once admission within the retention window, not exactly-once execution.
// A crash between the side effect and receipt commit is UNKNOWN and is never replayed.
public sealed class OperationJournal
{
    private const int SchemaVersion = OperationReceipt.CurrentSchemaVersion;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly ProjectPaths _paths;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly OperationJournalLimits _limits;
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    // Host composition metadata only, NOT root authentication or native authority.
    public ProjectPaths Paths => _paths;

    public OperationJournal(ProjectPaths paths, TimeProvider? time = null, OperationJournalLimits? limits = null)
    {
        _paths = paths;
        _directory = Path.Combine(paths.RootDirectory, ".autorun-state", CurrentUserIdentity.Key, "operations");
        _time = time ?? TimeProvider.System;
        _limits = limits ?? new OperationJournalLimits();
        if (_limits.MaximumEntries < 1 || _limits.MaximumResultBytes < 1
            || _limits.MaximumReceiptBytes < _limits.MaximumResultBytes + 8192
            || _limits.MaximumBytes < _limits.MaximumReceiptBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    public static string NormalizeId(string? operationId)
    {
        if (operationId?.Length != 32 || !Guid.TryParseExact(operationId, "N", out var parsed)
            || parsed.Version != 7)
        {
            throw new AgentCommandException("invalid_operation_id", "Mutation requires a 32-character UUIDv7 operationId.");
        }

        return parsed.ToString("N");
    }

    public Task<AgentResponse> RunAsync(
        AgentRequest request, Func<CancellationToken, Task<AgentResponse>> execute,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, (_, token) => execute(token), false, cancellationToken);

    // A callback-scoped input snapshot, NOT confirmation, authentication or native authority.
    // Legacy callbacks retain their original closure semantics; only this API freezes payload.
    public Task<AgentResponse> RunWithContextAsync(
        AgentRequest request, Func<OperationExecutionContext, CancellationToken, Task<AgentResponse>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(execute);
        return RunCoreAsync(request, (context, token) => execute(context!, token), true, cancellationToken);
    }

    private async Task<AgentResponse> RunCoreAsync(
        AgentRequest request, Func<OperationExecutionContext?, CancellationToken, Task<AgentResponse>> execute,
        bool includeContext, CancellationToken cancellationToken)
    {
        var id = NormalizeId(request.OperationId);
        if (includeContext) { request = request with { OperationId = id, Payload = request.Payload?.Clone() }; }
        var fingerprint = Fingerprint(request);
        string? physicalRoot = null; DateTimeOffset acceptedAt = default;
        await using (var lease = await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false))
        {
            CleanOrphanedTemporaryFiles();
            var (metadata, metadataPersisted) = await ReadMetadataAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadReceiptAsync(id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new AgentCommandException("operation_id_conflict", "This operationId was already used with different arguments.", operationId: id);
                }

                return Replay(request.Id, Observe(existing));
            }

            var now = _time.GetUtcNow();
            var floor = Math.Max(metadata.MinimumTimestamp, (now - Retention).ToUnixTimeMilliseconds());
            var timestamp = Timestamp(id);
            if (timestamp <= floor)
            {
                throw new AgentCommandException("operation_expired", "Operation ID is outside the retained window; inspect current state before a new intent.", operationId: id);
            }

            if (timestamp > (now + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds())
            {
                throw new AgentCommandException("invalid_operation_id", "Operation ID is too far in the future. Check the system clock.", operationId: id);
            }

            Directory.CreateDirectory(_directory);
            var entries = new List<FileInfo>();
            var expired = new List<(string Path, long Timestamp)>();
            long reservedBytes = 0;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.operation.json"))
            {
                var name = Path.GetFileName(file)[..^".operation.json".Length];
                string storedId;
                try { storedId = NormalizeId(name); }
                catch (AgentCommandException exception) { throw Corrupt(exception.Message); }
                if (Timestamp(storedId) <= floor)
                {
                    expired.Add((file, Timestamp(storedId)));
                }
                else
                {
                    var info = new FileInfo(file);
                    entries.Add(info);
                    var receipt = await ReadReceiptAsync(storedId, cancellationToken).ConfigureAwait(false)
                        ?? throw Corrupt("Receipt disappeared while holding the root lease.");
                    reservedBytes += receipt.State == OperationState.Running
                        ? _limits.MaximumReceiptBytes : info.Length;
                }
            }

            // Retire only timestamps actually removed, NOT wall-clock now-retention.
            // A clock jump with no receipts to prune must not wedge later fresh writes.
            var retired = expired.Count == 0 ? metadata.MinimumTimestamp
                : Math.Max(metadata.MinimumTimestamp, expired.Max(file => file.Timestamp));
            // Persist the watermark before pruning, but do not republish an unchanged
            // image for every new intent. Besides needless I/O, Windows MoveFileEx can
            // reject replacement of an open target even when its reader shares Delete.
            if (!metadataPersisted || retired != metadata.MinimumTimestamp)
            {
                await WriteJsonAsync(MetadataPath, new JournalMetadata(SchemaVersion, retired), cancellationToken).ConfigureAwait(false);
            }
            foreach (var file in expired)
            {
                // Persist the retirement watermark BEFORE removing verified UUID files.
                VerifyOwnedFile(file.Path);
                File.Delete(file.Path);
            }

            if (entries.Count >= _limits.MaximumEntries
                || reservedBytes + _limits.MaximumReceiptBytes > _limits.MaximumBytes)
            {
                // Never evict a fresh receipt just to accept more writes: that would let
                // its operationId execute twice. Reads/monitoring remain available.
                throw new AgentCommandException("operation_store_full", "Operation receipt capacity reached; no mutation was executed.", operationId: id);
            }

            var pending = new OperationReceipt(SchemaVersion, id, request.Command.ToLowerInvariant(),
                Short(request.Name, 256), fingerprint, InstanceId, OperationState.Running, now);
            if (includeContext)
            {
                lease.Require(_paths); physicalRoot = PhysicalDirectory.Resolve(_paths.RootDirectory); acceptedAt = pending.AcceptedAt;
                lease.Require(_paths);
            }
            _active.TryAdd(id, 0);
            try
            {
                await WriteJsonAsync(ReceiptPath(id), pending, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _active.TryRemove(id, out _);
                throw;
            }
        }

        try
        {
            AgentResponse response;
            if (cancellationToken.IsCancellationRequested)
            {
                response = new AgentResponse(request.Id, false, Error: "Cancelled before execution.", ErrorCode: "not_executed", OperationId: id);
            }
            else
            {
                try
                {
                    var context = includeContext
                        ? new OperationExecutionContext(physicalRoot!, id, fingerprint, InstanceId, acceptedAt, request) : null;
                    try
                    {
                        context?.RequireActiveFor(_paths);
                        response = (await execute(context, cancellationToken).ConfigureAwait(false)) with { OperationId = id };
                    }
                    finally { context?.End(); }
                }
                catch (Exception exception)
                {
                    response = new AgentResponse(request.Id, false, Error: Short(exception.Message, 4096),
                        ErrorCode: "outcome_unknown", OperationId: id);
                }
            }

            if (IsUncertain(response, request.Command) && response.ErrorCode != "outcome_unknown")
            {
                response = response with
                {
                    ErrorCode = "outcome_unknown",
                    Error = $"Outcome unknown ({response.ErrorCode}): {response.Error}. Query operation {id}; do not retry with a new ID.",
                };
            }

            await using var lease = await RootWriteLease.AcquireAsync(_paths, CancellationToken.None).ConfigureAwait(false);
            var pending = await ReadReceiptAsync(id, CancellationToken.None).ConfigureAwait(false)
                ?? throw Corrupt("Accepted operation receipt disappeared.");
            var resultBytes = JsonSerializer.SerializeToUtf8Bytes(response, AgentProtocol.JsonOptions);
            var omitted = resultBytes.Length > _limits.MaximumResultBytes;
            var safeResponse = omitted
                ? response with { Data = null, Error = Short(response.Error, 4096) }
                : response;
            var state = IsUncertain(response, request.Command) ? OperationState.Unknown : OperationState.Completed;
            await WriteJsonAsync(ReceiptPath(id), pending with
            {
                State = state, CompletedAt = _time.GetUtcNow(), Response = safeResponse, ResultOmitted = omitted,
            }, CancellationToken.None).ConfigureAwait(false);
            return response;
        }
        catch (Exception exception) when (exception is not AgentOutcomeUnknownException)
        {
            throw new AgentOutcomeUnknownException(id, exception);
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    public async Task<OperationReceipt?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var id = NormalizeId(operationId);
        await using var lease = await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        CleanOrphanedTemporaryFiles();
        _ = await ReadMetadataAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await ReadReceiptAsync(id, cancellationToken).ConfigureAwait(false);
        return receipt is null ? null : Observe(receipt);
    }

    private OperationReceipt Observe(OperationReceipt receipt) =>
        receipt.State == OperationState.Running
        && (receipt.OwnerInstanceId != InstanceId || !_active.ContainsKey(receipt.OperationId))
            ? receipt with { State = OperationState.Unknown }
            : receipt;

    private static AgentResponse Replay(string requestId, OperationReceipt receipt)
    {
        if (receipt.State == OperationState.Running)
        {
            return new AgentResponse(requestId, false, Error: "The original operation is still running.",
                ErrorCode: "operation_in_progress", OperationId: receipt.OperationId, Replayed: true);
        }

        if (receipt.State == OperationState.Unknown)
        {
            return new AgentResponse(requestId, false, Error: "The original outcome is unknown. Inspect state; do not replay with a new ID.",
                ErrorCode: "outcome_unknown", OperationId: receipt.OperationId, Replayed: true);
        }

        if (receipt.ResultOmitted || receipt.Response is null)
        {
            return new AgentResponse(requestId, false, Error: "Operation completed; detailed result exceeded receipt retention size. Query receipt and reload state.",
                ErrorCode: "operation_result_unavailable", Revision: receipt.Response?.Revision,
                OperationId: receipt.OperationId, Replayed: true);
        }

        return receipt.Response with { Id = requestId, OperationId = receipt.OperationId, Replayed = true };
    }

    private static bool IsUncertain(AgentResponse response, string command) => !response.Ok
        && !(response.ErrorCode is ("invalid_argument" or "invalid_operation_id" or "unknown_command" or "revision_conflict"
            or "configuration_error" or "not_found" or "not_executed" or "authorization_required" or "configuration_busy")
            // These codes are emitted ONLY before startup-adopt reaches AddTaskAsync.
            // Do not generalize their no-write guarantee to other commands or I/O errors.
            || string.Equals(command, "startup-adopt", StringComparison.OrdinalIgnoreCase)
            && response.ErrorCode is ("startup_entry_changed" or "startup_inventory_incomplete"
                or "startup_preflight_unavailable" or "startup_preflight_permission_denied"));

    private async Task<(JournalMetadata Metadata, bool Persisted)> ReadMetadataAsync(CancellationToken cancellationToken)
    {
        var metadata = await ReadJsonAsync<JournalMetadata>(MetadataPath, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            if (Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, "*.operation.json").Any())
            {
                throw Corrupt("Receipt metadata is missing; refusing to admit another mutation.");
            }

            return (new JournalMetadata(SchemaVersion, long.MinValue), false);
        }

        if (metadata.SchemaVersion != SchemaVersion)
        {
            throw Corrupt("Unsupported operation receipt schema.");
        }

        return (metadata, true);
    }

    private async Task<OperationReceipt?> ReadReceiptAsync(string id, CancellationToken cancellationToken)
    {
        var receipt = await ReadJsonAsync<OperationReceipt>(ReceiptPath(id), cancellationToken).ConfigureAwait(false);
        if (receipt is not null && (receipt.SchemaVersion != SchemaVersion || receipt.OperationId != id
            || receipt.Fingerprint?.Length != 64 || !Enum.IsDefined(receipt.State)
            || string.IsNullOrWhiteSpace(receipt.OwnerInstanceId)))
        {
            throw Corrupt("Invalid operation receipt identity/state.");
        }

        return receipt;
    }

    private async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > _limits.MaximumReceiptBytes)
            {
                throw Corrupt("Operation receipt exceeds its size limit.");
            }

            return await JsonSerializer.DeserializeAsync<T>(stream, AgentProtocol.JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw Corrupt("Operation receipt is null.");
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
        catch (JsonException exception) { throw Corrupt(exception.Message); }
    }

    private void CleanOrphanedTemporaryFiles()
    {
        // All journal writers hold the root lease. No live writer can own one of these
        // files while this method runs; only exact journal-generated names are removed.
        if (!Directory.Exists(_directory)) { return; }
        foreach (var file in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            var name = Path.GetFileName(file)[..^4];
            var separator = name.LastIndexOf('.');
            if (separator < 0 || !Guid.TryParseExact(name[(separator + 1)..], "N", out _)) { continue; }
            var destination = name[..separator];
            if (destination != "metadata.json")
            {
                if (!destination.EndsWith(".operation.json", StringComparison.Ordinal)) { continue; }
                try { _ = NormalizeId(destination[..^".operation.json".Length]); }
                catch (AgentCommandException) { continue; }
            }
            VerifyOwnedFile(file);
            File.Delete(file);
        }
    }

    private void VerifyOwnedFile(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(_directory), StringComparison.Ordinal))
        {
            throw Corrupt("Refusing to remove a file outside the operation directory.");
        }
    }

    private async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AgentProtocol.JsonOptions);
        if (bytes.Length > _limits.MaximumReceiptBytes)
        {
            throw Corrupt("Operation receipt exceeds its size limit.");
        }

        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var stage = "temporary_create";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                stage = "temporary_write";
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stage = "temporary_flush";
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            stage = "publication";
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Server-side diagnostic context only. Preserve type/HResult and never retry
            // an uncertain namespace publication or expose full paths in the wire error.
            exception.Data["OperationJournal.Stage"] = stage;
            exception.Data["OperationJournal.Path"] = path;
            exception.Data["OperationJournal.TemporaryPath"] = temporary;
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                VerifyOwnedFile(temporary);
                File.Delete(temporary);
            }
        }
    }

    internal static string Fingerprint(AgentRequest request)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject();
            writer.WriteString("command", request.Command.ToLowerInvariant());
            writer.WriteString("name", request.Name);
            writer.WriteString("expectedRevision", request.ExpectedRevision);
            writer.WritePropertyName("payload");
            if (request.Payload is JsonElement payload) { WriteCanonical(writer, payload); }
            else { writer.WriteNullValue(); }
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(memory.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) { WriteCanonical(writer, item); }
            writer.WriteEndArray();
        }
        else { element.WriteTo(writer); }
    }

    private string MetadataPath => Path.Combine(_directory, "metadata.json");
    private string ReceiptPath(string id) => Path.Combine(_directory, id + ".operation.json");
    private static long Timestamp(string id) => Convert.ToInt64(id[..12], 16);
    private static string? Short(string? value, int maximum) => value?.Length > maximum ? value[..maximum] : value;
    private static AgentCommandException Corrupt(string message) => new("operation_store_corrupt", message);
    private sealed record JournalMetadata(int SchemaVersion, long MinimumTimestamp);
}
