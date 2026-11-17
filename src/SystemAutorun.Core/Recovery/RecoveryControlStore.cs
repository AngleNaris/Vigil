using System.ComponentModel;
using SystemAutorun.Core.Configuration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Recovery;

public enum RecoveryAttemptOutcome { Admitted, Succeeded, Failed, OutcomeUnknown }

public sealed record RecoveryTaskState(
    string TaskName, bool Paused = false, DateTimeOffset? PausedAt = null,
    string? AttemptId = null, string? TaskIdentity = null, DateTimeOffset? LastAttemptAt = null,
    RecoveryAttemptOutcome? Outcome = null, DateTimeOffset? CompletedAt = null, string? Detail = null,
    string? IntentId = null);

public sealed record RecoveryControlSnapshot(string Revision, IReadOnlyList<RecoveryTaskState> Tasks, bool Initialized) : IRevisionedResult;
public sealed record RecoveryAdmission(RecoveryTaskState? Attempt, RecoveryControlSnapshot State);

// Durable intent/admission, NOT proof that an OS start/stop happened. All read-modify-write
// operations take the same root lease as configuration writers. No actions run in this store.
public sealed class RecoveryControlStore
{
    public const int MaximumEntries = 1024;
    public const int MaximumBytes = 512 * 1024;
    public static readonly TimeSpan MinimumAttemptInterval = TimeSpan.FromMinutes(2);
    private readonly ProjectPaths _paths;
    private readonly string _rootId;
    private readonly TimeProvider _clock;
    private string DirectoryPath => Path.Combine(_paths.RootDirectory, ".autorun-state", CurrentUserIdentity.Key);
    public string FilePath => Path.Combine(DirectoryPath, "recovery-v1.json");

    public RecoveryControlStore(ProjectPaths paths, TimeProvider? clock = null)
    {
        _paths = new ProjectPaths(PhysicalDirectory.Resolve(paths.RootDirectory));
        _rootId = AgentProtocol.ForRoot(_paths.RootDirectory).RootId;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<RecoveryControlSnapshot> ReadAsync(CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null)
    {
        await using var lease = await AcquireIfNeededAsync(heldLease, cancellationToken).ConfigureAwait(false);
        return await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
    }

    // Explicit initialization/reset acknowledgement only. Never call this from background
    // recovery or silently repair missing state: a missing file may have contained stop intent.
    public async Task<RecoveryControlSnapshot> InitializeEmptyAsync(string expectedRevision, CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = await AcquireIfNeededAsync(heldLease, cancellationToken).ConfigureAwait(false);
        var state = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        CheckRevision(state, expectedRevision);
        return state.Initialized ? state : await WriteLockedAsync([], cancellationToken).ConfigureAwait(false);
    }

    // First manual control on missing state initializes every configured name as PAUSED.
    // It does not erase unknown historical stop intent or make other nodes auto-recoverable.
    public async Task<RecoveryControlSnapshot> InitializePausedAsync(IReadOnlyList<string> names, string expectedRevision,
        CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = await AcquireIfNeededAsync(heldLease, cancellationToken).ConfigureAwait(false);
        var state = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        CheckRevision(state, expectedRevision);
        return state.Initialized ? state : await WriteLockedAsync(names.Select(name =>
            new RecoveryTaskState(name, true, _clock.GetUtcNow(), IntentId: AgentProtocol.NewOperationId())).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<RootWriteLease.Held?> AcquireIfNeededAsync(RootWriteLease.Held? heldLease, CancellationToken cancellationToken)
    {
        if (heldLease is not null) { heldLease.Require(_paths); return null; }
        return await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireInitialized(RecoveryControlSnapshot state)
    {
        if (!state.Initialized) { throw new InvalidDataException("Recovery control state is missing; explicit initialization/repair is required, not automatic reset."); }
    }

    // Pause is name-scoped intent and survives target edits/root relocation. Resuming never
    // erases attempt/cooldown evidence. A UI/CLI edit supplies the last recovery revision.
    public async Task<RecoveryControlSnapshot> SetPausedAsync(string name, bool paused,
        string? expectedRevision = null, CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null,
        bool recordIntent = false)
    {
        ValidateName(name);
        await using var lease = await AcquireIfNeededAsync(heldLease, cancellationToken).ConfigureAwait(false);
        var state = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        CheckRevision(state, expectedRevision);
        RequireInitialized(state);
        var before = Find(state, name);
        if (before is null && !paused || !recordIntent && before is not null && before.Paused == paused) { return state; }
        var after = (before ?? new(name)) with
        { Paused = paused, PausedAt = paused ? _clock.GetUtcNow() : null, IntentId = AgentProtocol.NewOperationId() };
        return await ReplaceLockedAsync(state, after, cancellationToken).ConfigureAwait(false);
    }

    // The caller must additionally authorize fresh fault + current policy/configuration
    // under its shared task lock; this only arbitrates durable pause/cooldown admission.
    public Task<RecoveryAdmission> TryAdmitAutomaticAsync(string name, string identity,
        string? expectedRevision = null, CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null) =>
        AdmitAsync(name, identity, manual: false, expectedRevision, cancellationToken, heldLease);

    // Manual start is a new explicit intent, clears pause, and reserves its attempt atomically.
    public Task<RecoveryAdmission> BeginManualStartAsync(string name, string identity,
        string? expectedRevision = null, CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null) =>
        AdmitAsync(name, identity, manual: true, expectedRevision, cancellationToken, heldLease);

    private async Task<RecoveryAdmission> AdmitAsync(string name, string identity, bool manual,
        string? expectedRevision, CancellationToken cancellationToken, RootWriteLease.Held? heldLease)
    {
        ValidateName(name); ValidateIdentity(identity);
        await using var lease = await AcquireIfNeededAsync(heldLease, cancellationToken).ConfigureAwait(false);
        var state = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        CheckRevision(state, expectedRevision);
        if (!manual && !state.Initialized) { return new(null, state); }
        RequireInitialized(state);
        var before = Find(state, name);
        var now = _clock.GetUtcNow();
        // Never launder a backward clock adjustment into a fresh attempt.
        if (!manual && (before?.Paused == true || before?.Outcome == RecoveryAttemptOutcome.Admitted || before?.LastAttemptAt is { } last
            && now - last < MinimumAttemptInterval))
        { return new(null, state); }
        var attemptId = AgentProtocol.NewOperationId();
        var attempt = (before ?? new(name)) with
        {
            Paused = false, PausedAt = null, AttemptId = attemptId, TaskIdentity = identity,
            IntentId = manual ? attemptId : before?.IntentId,
            LastAttemptAt = now, Outcome = RecoveryAttemptOutcome.Admitted, CompletedAt = null, Detail = null,
        };
        var committed = await ReplaceLockedAsync(state, attempt, cancellationToken).ConfigureAwait(false);
        return new(attempt, committed);
    }

    // Re-read after the OS call. Never write back the stale admission snapshot: a concurrent
    // Stop may have paused this task. Superseded completions must not touch the new attempt.
    public async Task<RecoveryControlSnapshot> CompleteAsync(string name, string attemptId,
        RecoveryAttemptOutcome outcome, string? detail = null, CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null)
    {
        ValidateName(name); attemptId = OperationJournal.NormalizeId(attemptId);
        if (outcome is RecoveryAttemptOutcome.Admitted || !Enum.IsDefined(outcome))
        { throw new ArgumentOutOfRangeException(nameof(outcome)); }
        await using var lease = await AcquireIfNeededAsync(heldLease, cancellationToken).ConfigureAwait(false);
        var state = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        RequireInitialized(state);
        var before = Find(state, name);
        if (before?.AttemptId != attemptId || before.Outcome != RecoveryAttemptOutcome.Admitted) { return state; }
        return await ReplaceLockedAsync(state, before with
        {
            Outcome = outcome, CompletedAt = _clock.GetUtcNow(), Detail = detail is { Length: > 512 } ? detail[..512] : detail,
        }, cancellationToken).ConfigureAwait(false);
    }

    // Called only by the new Agent instance after acquiring its exclusive instance lease.
    // This changes bookkeeping, does not replay an admitted side effect or reset cooldown.
    public async Task<RecoveryControlSnapshot> MarkInterruptedAttemptsUnknownAsync(CancellationToken cancellationToken = default, RootWriteLease.Held? heldLease = null)
    {
        await using var lease = await AcquireIfNeededAsync(heldLease, cancellationToken).ConfigureAwait(false);
        var state = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Tasks.Any(task => task.Outcome == RecoveryAttemptOutcome.Admitted)) { return state; }
        return await WriteLockedAsync(state.Tasks.Select(task => task.Outcome == RecoveryAttemptOutcome.Admitted
            ? task with { Outcome = RecoveryAttemptOutcome.OutcomeUnknown, CompletedAt = _clock.GetUtcNow(), Detail = "Agent restarted before outcome was committed; do not replay this attempt." }
            : task).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static RecoveryTaskState? Find(RecoveryControlSnapshot state, string name) =>
        state.Tasks.FirstOrDefault(task => string.Equals(task.TaskName, name, StringComparison.OrdinalIgnoreCase));

    private static void CheckRevision(RecoveryControlSnapshot state, string? expected)
    {
        if (expected is not null && expected != state.Revision)
        { throw new AgentCommandException("recovery_revision_conflict", "Recovery intent changed; read its current state before retrying.", revision: state.Revision); }
    }

    private async Task<RecoveryControlSnapshot> ReplaceLockedAsync(RecoveryControlSnapshot state,
        RecoveryTaskState replacement, CancellationToken cancellationToken)
    {
        var tasks = state.Tasks.Where(task => !string.Equals(task.TaskName, replacement.TaskName, StringComparison.OrdinalIgnoreCase))
            .Append(replacement).ToArray();
        return await WriteLockedAsync(tasks, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecoveryControlSnapshot> ReadLockedAsync(CancellationToken cancellationToken)
    {
        if (!VerifyDirectory(create: false)) { return Snapshot([], null); }
        // A process/power failure can leave an uncommitted intent. Never select a guessed
        // winner, ignore it as 'no pause', or delete a merely matching-prefix file.
        if (Directory.EnumerateFileSystemEntries(DirectoryPath, ".recovery-*.tmp").Any())
        { throw new InvalidDataException("An uncommitted recovery state exists; inspect/repair it before recovery can continue."); }
        if (!VerifyFile()) { return Snapshot([], null); }
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (stream.Length is <= 0 or > MaximumBytes) { throw new InvalidDataException("Recovery state is empty or exceeds its bound."); }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(bytes, AgentProtocol.JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Recovery state is corrupt; it was not reset.", exception); }
        if (document?.SchemaVersion != 1 || document.Tasks is null) { throw new InvalidDataException("Unsupported recovery state."); }
        try { ValidateTasks(document.Tasks); }
        catch (Exception exception) when (exception is ArgumentException or AgentCommandException)
        { throw new InvalidDataException("Recovery state is corrupt; it was not reset.", exception); }
        return Snapshot(document.Tasks, bytes);
    }

    private async Task<RecoveryControlSnapshot> WriteLockedAsync(IReadOnlyList<RecoveryTaskState> tasks, CancellationToken cancellationToken)
    {
        ValidateTasks(tasks);
        var ordered = tasks.OrderBy(task => task.TaskName, StringComparer.OrdinalIgnoreCase).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, ordered), AgentProtocol.JsonOptions);
        if (bytes.Length > MaximumBytes) { throw new InvalidDataException("Recovery capacity reached; no intent was discarded."); }
        VerifyDirectory(create: true); VerifyFile();
        var temporary = Path.Combine(DirectoryPath, $".recovery-{Guid.NewGuid():N}.tmp");
        var temporaryCreated = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            VerifyDirectory(create: false); VerifyFile();
            // Same-directory atomic publication, requesting metadata write-through as well
            // as the already-flushed file contents. Hardware/filesystem guarantees still apply.
            if (!MoveFileEx(ExtendedPath(temporary), ExtendedPath(FilePath), 0x1 | 0x8)) // REPLACE_EXISTING | WRITE_THROUGH
            { throw new IOException("Recovery state publication failed.", new Win32Exception(Marshal.GetLastWin32Error())); }
            return Snapshot(ordered, bytes);
        }
        finally
        {
            // Delete only this call's exact CreateNew temporary file after rechecking its parent.
            VerifyDirectory(create: false);
            if (Path.GetDirectoryName(Path.GetFullPath(temporary)) != DirectoryPath) { throw new InvalidDataException("Unexpected recovery temporary path."); }
            if (temporaryCreated && File.Exists(temporary)) { File.Delete(temporary); }
        }
    }

    private RecoveryControlSnapshot Snapshot(IReadOnlyList<RecoveryTaskState> tasks, byte[]? bytes)
    {
        var prefix = Encoding.UTF8.GetBytes(_rootId + ":" + CurrentUserIdentity.Key + ":");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix); hash.AppendData(bytes ?? "missing"u8.ToArray());
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), tasks, bytes is not null);
    }

    private bool VerifyDirectory(bool create)
    {
        var path = _paths.RootDirectory;
        foreach (var part in new[] { ".autorun-state", CurrentUserIdentity.Key })
        {
            path = Path.Combine(path, part);
            var attributes = Attributes(path);
            if (attributes is null)
            {
                if (!create) { return false; }
                Directory.CreateDirectory(path);
                attributes = File.GetAttributes(path);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            { throw new InvalidDataException("Recovery state directories must be real directories inside this root."); }
        }
        return true;
    }

    private bool VerifyFile()
    {
        var attributes = Attributes(FilePath);
        if (attributes is null) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        { throw new InvalidDataException("Recovery state must be a regular file inside this root."); }
        return true;
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
        { throw new ArgumentException("A task name of 1–256 non-control characters is required.", nameof(name)); }
    }

    private static void ValidateIdentity(string identity)
    {
        if (identity is null || identity.Length != 64 || identity.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        { throw new ArgumentException("A workload identity hash is required.", nameof(identity)); }
    }

    private static void ValidateTasks(IReadOnlyList<RecoveryTaskState> tasks)
    {
        if (tasks.Count > MaximumEntries) { throw new InvalidDataException("Recovery capacity reached; no intent was discarded."); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (task is null) { throw new InvalidDataException("Null recovery entry."); }
            ValidateName(task.TaskName);
            if (task.IntentId is not null && OperationJournal.NormalizeId(task.IntentId) != task.IntentId)
            { throw new InvalidDataException("Noncanonical recovery intent ID."); }
            if (!names.Add(task.TaskName) || task.Paused != (task.PausedAt is not null) || task.Detail?.Length > 512)
            { throw new InvalidDataException("Invalid or duplicate recovery entry."); }
            if (task.AttemptId is null)
            {
                if (task.TaskIdentity is not null || task.LastAttemptAt is not null || task.Outcome is not null || task.CompletedAt is not null || task.Detail is not null)
                { throw new InvalidDataException("Incomplete recovery attempt."); }
            }
            else
            {
                if (OperationJournal.NormalizeId(task.AttemptId) != task.AttemptId) { throw new InvalidDataException("Noncanonical recovery attempt ID."); }
                ValidateIdentity(task.TaskIdentity ?? "");
                if (task.LastAttemptAt is null || task.Outcome is null || !Enum.IsDefined(task.Outcome.Value)
                    || (task.Outcome == RecoveryAttemptOutcome.Admitted) != (task.CompletedAt is null))
                { throw new InvalidDataException("Incomplete recovery outcome."); }
            }
        }
    }

    private static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);

    private sealed record Document(int SchemaVersion, IReadOnlyList<RecoveryTaskState> Tasks);
}
