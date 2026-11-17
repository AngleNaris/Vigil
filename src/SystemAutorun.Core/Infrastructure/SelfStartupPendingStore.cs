using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Infrastructure;

public sealed class SelfStartupPendingStoreException(string code, Exception? inner = null)
    : IOException("Self startup recovery storage is blocked: " + code + ".", inner)
{
    public string Code { get; } = code;
}

// Independent per-root recovery hint, NOT a Journal owner or mutation lease.
// Read never creates/cleans files. Writers share the existing cooperative root
// lease, never overwrite pending, and never replay or infer a business outcome.
public sealed class SelfStartupPendingStore
{
    public const string FileName = "autorun.self-startup.pending.json";
    public const int MaximumBytes = 2048;
    private static readonly JsonSerializerOptions Json = new(AgentProtocol.JsonOptions)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };
    private readonly ProjectPaths _paths;
    private readonly Action<string>? _checkpoint;

    public SelfStartupPendingStore(ProjectPaths paths) : this(paths, null) { }
    // Private deterministic crash/IO seam; not a production extension point.
    private SelfStartupPendingStore(ProjectPaths paths, Action<string>? checkpoint)
    { _paths = paths ?? throw new ArgumentNullException(nameof(paths)); _checkpoint = checkpoint; }

    public async Task<SelfStartupPendingSnapshot> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var paths = ProjectPaths.Resolve(_paths.RootDirectory);
            return (await ReadFileAsync(paths, AgentProtocol.ForRoot(paths.RootDirectory).RootId, token).ConfigureAwait(false)).Snapshot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Unavailable("pending_storage_unavailable"); }
    }

    // Caller supplies the original ID AFTER explicit confirmation and awaits this
    // return BEFORE sending. Any failure (including after rename) means DO NOT SEND.
    public async Task<Reservation> ReserveAsync(string operationId, bool enabled,
        CancellationToken token = default, TimeSpan? leaseTimeout = null)
    {
        var id = OperationJournal.NormalizeId(operationId); token.ThrowIfCancellationRequested();
        string? temporary = null; var ownsTemporary = false;
        try
        {
            var paths = ProjectPaths.Resolve(_paths.RootDirectory);
            await using var held = await RootWriteLease.AcquireAsync(paths, token, leaseTimeout).ConfigureAwait(false);
            held.Require(paths);
            var rootId = AgentProtocol.ForRoot(paths.RootDirectory).RootId;
            var before = (await ReadFileAsync(paths, rootId, token).ConfigureAwait(false)).Snapshot;
            if (before.BlocksSubmission)
            { throw new SelfStartupPendingStoreException(before.Reason ?? "pending_exists"); }
            var record = new SelfStartupPendingOperation
            { SchemaVersion = 1, RootId = rootId, OperationId = id, Enabled = enabled, ReservedAt = DateTimeOffset.UtcNow };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            if (bytes.Length is <= 0 or > MaximumBytes) { throw new SelfStartupPendingStoreException("pending_invalid"); }
            var destination = Path.Combine(paths.RootDirectory, FileName);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ownsTemporary = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(flushToDisk: true);
            }
            _checkpoint?.Invoke("flushed"); token.ThrowIfCancellationRequested();
            // Same-directory publication, no replacement/fallback/retry. This is
            // process-crash recovery, not a power-loss/hardware durability promise.
            File.Move(temporary, destination, overwrite: false);
            _checkpoint?.Invoke("published");
            var after = await ReadFileAsync(paths, rootId, token).ConfigureAwait(false);
            if (after.Snapshot.State != SelfStartupPendingState.Present || after.Bytes is null || !bytes.AsSpan().SequenceEqual(after.Bytes))
            { throw new SelfStartupPendingStoreException("pending_publication_unverified"); }
            return new Reservation(this, record, bytes);
        }
        catch (SelfStartupPendingStoreException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        { throw new SelfStartupPendingStoreException("pending_storage_unavailable", error); }
        finally
        {
            // Only the exact private sibling created by this attempt is eligible.
            // Never clean the destination or another attempt's orphan temporary.
            if (ownsTemporary && temporary is not null) { DeleteOwnTemporary(temporary); }
        }
    }

    // Only a successful fresh reservation creates this capability. Read/restart
    // returns no capability. Abandoning it does NOT clear anything or own resources.
    public sealed class Reservation
    {
        private readonly SelfStartupPendingStore _store;
        private readonly byte[] _bytes;
        private int _releaseAttempted;
        internal Reservation(SelfStartupPendingStore store, SelfStartupPendingOperation operation, byte[] bytes)
        { _store = store; Operation = operation; _bytes = bytes; }
        public SelfStartupPendingOperation Operation { get; }

        // Caller MUST independently establish a proven pre-send failure or fresh
        // definitive ACK first. This method supplies no such proof. NEVER invoke
        // for uncertain exchanges, recovery queries, Completed or historical Data.
        public Task ReleaseAfterKnownOutcomeAsync(CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref _releaseAttempted, 1) != 0)
            { throw new InvalidOperationException("A pending reservation release is single-attempt only."); }
            return _store.ReleaseAsync(Operation, _bytes, token);
        }
    }

    private async Task ReleaseAsync(SelfStartupPendingOperation record, byte[] bytes, CancellationToken token)
    {
        try
        {
            var paths = ProjectPaths.Resolve(_paths.RootDirectory);
            await using var held = await RootWriteLease.AcquireAsync(paths, token).ConfigureAwait(false); held.Require(paths);
            if (AgentProtocol.ForRoot(paths.RootDirectory).RootId != record.RootId)
            { throw new SelfStartupPendingStoreException("pending_changed"); }
            var before = await ReadFileAsync(paths, record.RootId, token).ConfigureAwait(false);
            if (before.Snapshot.State != SelfStartupPendingState.Present || before.Bytes is null || !bytes.AsSpan().SequenceEqual(before.Bytes))
            { throw new SelfStartupPendingStoreException("pending_changed"); }
            token.ThrowIfCancellationRequested();
            File.Delete(Path.Combine(paths.RootDirectory, FileName));
            if ((await ReadFileAsync(paths, record.RootId, token).ConfigureAwait(false)).Snapshot.State != SelfStartupPendingState.Missing)
            { throw new SelfStartupPendingStoreException("pending_clear_unverified"); }
        }
        catch (SelfStartupPendingStoreException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        { throw new SelfStartupPendingStoreException("pending_storage_unavailable", error); }
    }

    private static async Task<(SelfStartupPendingSnapshot Snapshot, byte[]? Bytes)> ReadFileAsync(ProjectPaths paths, string rootId, CancellationToken token)
    {
        var path = Path.Combine(paths.RootDirectory, FileName);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            { throw new IOException("Pending recovery requires an ordinary file."); }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaximumBytes) { throw new JsonException("Invalid pending bounds."); }
            var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize<SelfStartupPendingOperation>(bytes, Json) ?? throw new JsonException("Missing pending record.");
            if (value.SchemaVersion != 1 || value.RootId != rootId || OperationJournal.NormalizeId(value.OperationId) != value.OperationId
                || value.ReservedAt == default || value.ReservedAt.Offset != TimeSpan.Zero)
            { throw new JsonException("Invalid pending recovery binding."); }
            return (new(SelfStartupPendingState.Present, value), bytes);
        }
        catch (FileNotFoundException)
        {
            // Only a missing leaf in the still-valid same root means no pending.
            var current = ProjectPaths.Resolve(paths.RootDirectory);
            return AgentProtocol.ForRoot(current.RootDirectory).RootId == rootId
                ? (new(SelfStartupPendingState.Missing), null) : (Unavailable("pending_storage_unavailable"), null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or AgentCommandException)
        { return (Unavailable("pending_invalid"), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return (Unavailable("pending_storage_unavailable"), null); }
    }

    private static SelfStartupPendingSnapshot Unavailable(string reason) => new(SelfStartupPendingState.Unavailable, Reason: reason);
    private static void DeleteOwnTemporary(string path)
    {
        if (Path.GetFullPath(path) != path) { throw new IOException("Unexpected private pending path."); }
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            { throw new IOException("Private pending cleanup requires an ordinary file."); }
            File.Delete(path);
        }
        catch (FileNotFoundException) { }
    }
}
