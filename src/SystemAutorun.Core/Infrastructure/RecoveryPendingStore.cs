using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Infrastructure;

public sealed class RecoveryPendingStoreException(string code, Exception? inner = null)
    : IOException("Recovery resume storage is blocked: " + code + ".", inner)
{
    public string Code { get; } = code;
}

// Logical blocking is per physical root + Windows subject + case-insensitive name.
// The cooperative root write lease covers ONLY short publication/removal, never RPC.
// A hint is not a Journal receipt or an authorization/eligibility observation.
public sealed class RecoveryPendingStore
{
    public const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(AgentProtocol.JsonOptions)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };
    private readonly ProjectPaths _paths;
    private readonly string _name;
    private readonly string _subject;
    private readonly Action<string>? _checkpoint;
    public string FileName { get; }

    public RecoveryPendingStore(ProjectPaths paths, string taskName) : this(paths, taskName, null) { }
    // Deterministic private crash/IO seam, not a production extension point.
    private RecoveryPendingStore(ProjectPaths paths, string taskName, Action<string>? checkpoint)
    {
        if (!RecoveryPendingOperation.Name(taskName)) { throw new ArgumentException("A task name of 1–256 non-control characters is required.", nameof(taskName)); }
        _paths = paths ?? throw new ArgumentNullException(nameof(paths)); _name = taskName;
        _subject = CurrentUserIdentity.Key; _checkpoint = checkpoint;
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_subject + "\0" + taskName.ToUpperInvariant())));
        FileName = "autorun.recovery-resume." + scope + ".pending.json";
    }

    // Pure read: never creates a lock, cleans an orphan, initializes or repairs state.
    public async Task<RecoveryPendingSnapshot> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            RequireSubject(); var paths = ProjectPaths.Resolve(_paths.RootDirectory);
            return (await ReadFileAsync(paths, AgentProtocol.ForRoot(paths.RootDirectory).RootId, token).ConfigureAwait(false)).Snapshot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Unavailable("pending_storage_unavailable"); }
    }

    // Explicitly confirmed caller reserves AFTER observing eligibility and awaits
    // publication verification BEFORE sending this exact name/revision/original ID.
    public async Task<Reservation> ReserveAsync(string operationId, string expectedRevision,
        CancellationToken token = default, TimeSpan? leaseTimeout = null)
    {
        var id = OperationJournal.NormalizeId(operationId);
        if (!RecoveryPendingOperation.Digest(expectedRevision)) { throw new ArgumentException("A recovery revision is required.", nameof(expectedRevision)); }
        token.ThrowIfCancellationRequested(); string? temporary = null; var ownsTemporary = false;
        try
        {
            RequireSubject(); var paths = ProjectPaths.Resolve(_paths.RootDirectory);
            await using var held = await RootWriteLease.AcquireAsync(paths, token, leaseTimeout).ConfigureAwait(false); held.Require(paths);
            var rootId = AgentProtocol.ForRoot(paths.RootDirectory).RootId;
            var before = (await ReadFileAsync(paths, rootId, token).ConfigureAwait(false)).Snapshot;
            if (before.BlocksSubmission) { throw new RecoveryPendingStoreException(before.Reason ?? "pending_exists"); }
            var record = new RecoveryPendingOperation { SchemaVersion = 1, RootId = rootId, SubjectKey = _subject,
                TaskName = _name, ExpectedRevision = expectedRevision, OperationId = id, ReservedAt = DateTimeOffset.UtcNow };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            if (bytes.Length is <= 0 or > MaximumBytes) { throw new RecoveryPendingStoreException("pending_invalid"); }
            var destination = Path.Combine(paths.RootDirectory, FileName); temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ownsTemporary = true; await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(flushToDisk: true);
            }
            _checkpoint?.Invoke("flushed"); token.ThrowIfCancellationRequested();
            // Same-directory, no replacement/fallback/retry. Process-crash recovery,
            // not a hardware/power-loss guarantee or a defense against this user editing files.
            File.Move(temporary, destination, overwrite: false); _checkpoint?.Invoke("published");
            var after = await ReadFileAsync(paths, rootId, token).ConfigureAwait(false);
            if (after.Snapshot.State != RecoveryPendingState.Present || after.Bytes is null || !bytes.AsSpan().SequenceEqual(after.Bytes))
            { throw new RecoveryPendingStoreException("pending_publication_unverified"); }
            return new Reservation(this, record, bytes);
        }
        catch (RecoveryPendingStoreException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        { throw new RecoveryPendingStoreException("pending_storage_unavailable", error); }
        finally { if (ownsTemporary && temporary is not null) { DeleteOwnTemporary(temporary); } }
    }

    // Only a freshly verified reservation has this single-attempt capability.
    // Read/restart has none. Dropping the object neither clears nor owns resources.
    public sealed class Reservation
    {
        private readonly RecoveryPendingStore _store;
        private readonly byte[] _bytes;
        private int _releaseAttempted;
        internal Reservation(RecoveryPendingStore store, RecoveryPendingOperation operation, byte[] bytes)
        { _store = store; Operation = operation; _bytes = bytes; }
        public RecoveryPendingOperation Operation { get; }
        // Caller independently proves pre-send failure or a fresh definitive ACK.
        // NEVER invoke for unknown, historical queries/replays or current-state equality.
        public Task ReleaseAfterKnownOutcomeAsync(CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref _releaseAttempted, 1) != 0) { throw new InvalidOperationException("A pending reservation release is single-attempt only."); }
            return _store.ReleaseAsync(Operation, _bytes, token);
        }
    }

    private async Task ReleaseAsync(RecoveryPendingOperation record, byte[] bytes, CancellationToken token)
    {
        try
        {
            RequireSubject(); var paths = ProjectPaths.Resolve(_paths.RootDirectory);
            await using var held = await RootWriteLease.AcquireAsync(paths, token).ConfigureAwait(false); held.Require(paths);
            if (AgentProtocol.ForRoot(paths.RootDirectory).RootId != record.RootId) { throw new RecoveryPendingStoreException("pending_changed"); }
            var before = await ReadFileAsync(paths, record.RootId, token).ConfigureAwait(false);
            if (before.Snapshot.State != RecoveryPendingState.Present || before.Bytes is null || !bytes.AsSpan().SequenceEqual(before.Bytes))
            { throw new RecoveryPendingStoreException("pending_changed"); }
            token.ThrowIfCancellationRequested(); File.Delete(Path.Combine(paths.RootDirectory, FileName));
            if ((await ReadFileAsync(paths, record.RootId, token).ConfigureAwait(false)).Snapshot.State != RecoveryPendingState.Missing)
            { throw new RecoveryPendingStoreException("pending_clear_unverified"); }
        }
        catch (RecoveryPendingStoreException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        { throw new RecoveryPendingStoreException("pending_storage_unavailable", error); }
    }

    private async Task<(RecoveryPendingSnapshot Snapshot, byte[]? Bytes)> ReadFileAsync(ProjectPaths paths, string rootId, CancellationToken token)
    {
        var path = Path.Combine(paths.RootDirectory, FileName);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { throw new IOException("Pending requires an ordinary file."); }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaximumBytes) { throw new JsonException("Invalid pending bounds."); }
            _checkpoint?.Invoke("read-opened");
            var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<RecoveryPendingOperation>(bytes, Json);
            if (!RecoveryPendingOperation.Valid(record) || record!.RootId != rootId || record.SubjectKey != _subject
                || !record.TaskName.Equals(_name, StringComparison.OrdinalIgnoreCase)) { throw new JsonException("Invalid pending binding."); }
            return (new(RecoveryPendingState.Present, record), bytes);
        }
        catch (FileNotFoundException)
        {
            var current = ProjectPaths.Resolve(paths.RootDirectory); RequireSubject();
            return AgentProtocol.ForRoot(current.RootDirectory).RootId == rootId ? (new(RecoveryPendingState.Missing), null)
                : (Unavailable("pending_storage_unavailable"), null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { return (Unavailable("pending_invalid"), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return (Unavailable("pending_storage_unavailable"), null); }
    }

    private void RequireSubject()
    { if (CurrentUserIdentity.Key != _subject) { throw new UnauthorizedAccessException("Pending subject changed."); } }
    private static RecoveryPendingSnapshot Unavailable(string reason) => new(RecoveryPendingState.Unavailable, Reason: reason);
    private static void DeleteOwnTemporary(string path)
    {
        if (Path.GetFullPath(path) != path) { throw new IOException("Unexpected private pending path."); }
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { throw new IOException("Private pending cleanup requires an ordinary file."); }
            File.Delete(path);
        }
        catch (FileNotFoundException) { }
    }
}
