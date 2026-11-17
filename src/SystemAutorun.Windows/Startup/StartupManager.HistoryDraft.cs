using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

// A server-observed snapshot, NOT consent, a prepared RPC token, history or authority.
// Its pins are gone on return. The consuming owner must re-admit and revalidate it.
internal sealed record StartupHistoryDisableDraft(StartupHistoryPayload Candidate, StartupBackupRecord Record,
    string ConfigurationRevision, DateTimeOffset ObservedAt)
{
    public bool NativeWriteReady => false;
    public bool NativeWritePerformed => false;
    public bool ExecutionRequested => false;
    public bool Committed => false;
}

public sealed partial class StartupManager
{
    // Internal production primitive only. No transport route; no target probing or execution.
    // Only coordination files may be created. Missing archive directories are NOT created.
    internal async Task<StartupHistoryDisableDraft> CaptureHistoryDisableDraftAsync(string entryId, StartupInspectionRequest selection,
        string expectedRevision, CancellationToken token = default, Action<string>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(selection); var query = selection.Validate(entryId);
        if (selection.Source is not ("Startup" or "Common Startup") || selection.IsDisabled || selection.RebuildInput is not null
            || selection.Name.Length > 251 || expectedRevision is not { Length: 64 }
            || expectedRevision.Any(value => !char.IsAsciiHexDigit(value) || char.IsAsciiLetterUpper(value)))
        { throw new ArgumentException("A current shortcut selection and lowercase workspace revision are required."); }
        using var mutation = await StartupMutationLease.AcquireAsync(_paths, token).ConfigureAwait(false);
        await using var root = await SystemAutorun.Core.Infrastructure.RootWriteLease.AcquireAsync(_paths, token).ConfigureAwait(false);
        root.Require(_paths); EnsureNoUnresolvedTransaction(); EnsureCanModify(selection.Source);
        using var config = SshInputFileLease.Open(_paths.ConfigPath, ConfigurationDocumentRequest.MaximumBytes);
        await RequireHistoryDraftRevisionAsync(expectedRevision, token).ConfigureAwait(false);
        var configuration = await _configurationLoader.LoadAsync(_paths.RootDirectory, token).ConfigureAwait(false);
        var archiveBase = StartupShortcutPathLease.LocalPath(configuration.Settings.BackupDirectory);
        var archive = StartupShortcutPathLease.LocalPath(Path.Combine(archiveBase, "startup"));
        var rootPath = StartupShortcutPathLease.LocalPath(_paths.RootDirectory);
        using var rootPin = SshInputFileLease.OpenDirectory(rootPath);
        using var archiveBasePin = SshInputFileLease.OpenDirectory(archiveBase);
        using var archivePin = SshInputFileLease.OpenDirectory(archive);
        using var capture = StartupShortcutCapture.OpenReadOnly(selection.Source, selection.Name, _native, token);
        var scope = capture.Scope; var input = capture.Input; var actor = scope.ObserverSid!;
        StartupHistoryProtection.RequireActor(actor);
        if (scope.CaseSensitivity != "insensitive") { throw new InvalidOperationException("Disable draft requires known insensitive Startup semantics."); }
        StartupHistoryDirectory DirectoryFacts(string path)
        {
            var observed = StartupNativeSlotProbe.ObserveDirectory(path, token, () =>
            { root.Require(_paths); StartupHistoryProtection.RequireActor(actor); return true; });
            if (observed.State != "observed" || observed.DirectoryIdentity is null || observed.CaseSensitivity != "insensitive")
            { throw new InvalidOperationException("History context directory is unavailable or has unsupported case semantics."); }
            return new(path, observed.DirectoryIdentity, observed.CaseSensitivity);
        }
        var rootFacts = DirectoryFacts(rootPath); var baseFacts = DirectoryFacts(archiveBase); var archiveFacts = DirectoryFacts(archive);
        void VerifyContext()
        {
            root.Require(_paths); StartupHistoryProtection.RequireActor(actor); capture.VerifyCurrent(token);
            if (DirectoryFacts(rootPath) != rootFacts || DirectoryFacts(archiveBase) != baseFacts || DirectoryFacts(archive) != archiveFacts)
            { throw new InvalidOperationException("History context changed during draft projection."); }
        }
        VerifyContext(); checkpoint?.Invoke("draft_input_pinned"); VerifyContext();
        // CaptureInventory's own per-leaf pins are nested INSIDE our read-only original
        // pin. Its COM projection therefore describes this held object's bytes, not a
        // previously scanned path. No DELETE reservation conflicts with the COM reader.
        var observed = CaptureInventory(configuration, query, token);
        if (observed.Truncated || observed.SourceIssues.Any(issue => issue.Source == selection.Source))
        { throw new InvalidOperationException("Selected Startup source is incomplete or unavailable."); }
        var matches = observed.Entries.Where(entry => entry.Id == entryId && entry.Source == selection.Source
            && entry.Name == selection.Name && !entry.IsDisabled && entry.Origin == input.Origin).ToArray();
        if (matches.Length != 1 || matches[0].MetadataDigest != selection.MetadataDigest || matches[0].Error is not null || !matches[0].CanModify)
        { throw new InvalidOperationException("Displayed shortcut changed or is unreadable; capture a new selection."); }
        var entry = matches[0]; var id = Guid.NewGuid().ToString("N"); var when = DateTimeOffset.UtcNow;
        var record = new StartupBackupRecord { Id = id, Action = "disable", Disposition = "disabled", DisabledAt = when,
            Source = scope.Source, Name = capture.Name, Origin = input.Origin, BackupPath = Path.Combine(archive, id + ".lnk"),
            RawCommand = entry.RawCommand, Target = entry.Target, Arguments = entry.Arguments, WorkingDirectory = entry.WorkingDirectory };
        var binding = new StartupHistoryBinding(actor, rootFacts,
            new(scope.Source, scope.SourceKind, scope.KnownFolder!, scope.SourceUserSid,
                new(scope.DirectoryPath!, scope.DirectoryIdentity!, scope.CaseSensitivity)), baseFacts, archiveFacts,
            id, capture.Name, StartupHistoryFormat.RecordDigest(record), new(input.FileIdentity, input.Length, input.LinkCount, input.Sha256));
        var candidate = new StartupHistoryPayload(1, "candidate", binding, Guid.NewGuid().ToString("N"), 1, "disabled", when, null);
        RequireHistoryDraftLayout(record, archiveBase, scope.DirectoryPath!);
        StartupHistoryFormat.Validate(candidate); checkpoint?.Invoke("draft_metadata_read"); VerifyContext();
        await RequireHistoryDraftRevisionAsync(expectedRevision, token).ConfigureAwait(false);
        checkpoint?.Invoke("draft_verified"); VerifyContext();
        await RequireHistoryDraftRevisionAsync(expectedRevision, token).ConfigureAwait(false);
        return new(candidate, record, expectedRevision, DateTimeOffset.UtcNow);
    }

    internal Task<HistoryDisableTransaction> PrepareHistoryDisableAsync(StartupHistoryDisableDraft draft, Func<string>? ownedDirectory = null,
        CancellationToken token = default, Action<string>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ConfigurationRevision is not { Length: 64 } || draft.ConfigurationRevision.Any(value => !char.IsAsciiHexDigit(value) || char.IsAsciiLetterUpper(value)))
        { throw new ArgumentException("The captured workspace revision is required.", nameof(draft)); }
        return PrepareHistoryDisableCoreAsync(draft.Candidate, draft.Record, ownedDirectory, token, checkpoint, draft.ConfigurationRevision);
    }

    // Both fixed Startup folders matter, not just the selected source. No caller paths.
    private void RequireHistoryDraftLayout(StartupBackupRecord record, string archiveBase, string sourceDirectory) =>
        _ = StartupShortcutPathLease.Resolve(archiveBase, record.Id, record.Name, record.Origin, record.BackupPath, sourceDirectory,
            [_native.GetShortcutDirectory(UserStartupSource), _native.GetShortcutDirectory(CommonStartupSource)]);

    private async Task RequireHistoryDraftRevisionAsync(string? expected, CancellationToken token)
    {
        if (expected is null) { return; } // Existing raw internal bridge; not a prepared request.
        var current = await ConfigurationRevision.ReadAsync(_paths, cancellationToken: token).ConfigureAwait(false);
        if (current != expected) { throw new ConfigurationConflictException(expected, current); }
    }
}
