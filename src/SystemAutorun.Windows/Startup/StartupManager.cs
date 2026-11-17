using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

public sealed partial class StartupManager : IDisposable
{
    public const string UserStartupSource = "Startup";
    public const string CommonStartupSource = "Common Startup";
    public const string HkcuRunSource = "HKCU";
    public const string HklmRunSource = "HKLM";
    public const string HklmWow64RunSource = "HKLM-WOW64";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ProjectPaths _paths;
    private readonly ConfigurationLoader _configurationLoader;
    private readonly string _transactionPath;
    private readonly IStartupNativeAccess _native;
    private readonly IStartupMutationOperations _operations;

    public StartupManager(ProjectPaths paths, ConfigurationLoader? configurationLoader = null)
        : this(paths, WindowsStartupNativeAccess.Instance, configurationLoader) { }

    internal StartupManager(ProjectPaths paths, IStartupNativeAccess native, ConfigurationLoader? configurationLoader = null,
        IStartupMutationOperations? operations = null)
    {
        _paths = paths;
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _operations = operations ?? StartupMutationOperations.Instance;
        _configurationLoader = configurationLoader ?? new ConfigurationLoader();
        _transactionPath = paths.StartupBackupPath + ".transaction";
    }

    public async Task<StartupManagementSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationLoader.LoadAsync(_paths.RootDirectory, cancellationToken)
            .ConfigureAwait(false);
        return CaptureInventory(configuration, new StartupListQuery(), cancellationToken);
    }

    public async Task<StartupMutationResult> DisableAsync(
        string source,
        string name,
        string? origin,
        CancellationToken cancellationToken = default)
    {
        using var lease = await StartupMutationLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        EnsureNoUnresolvedTransaction();
        var configuration = await _configurationLoader.LoadAsync(_paths.RootDirectory, cancellationToken)
            .ConfigureAwait(false);
        var matches = CaptureInventory(configuration, new StartupListQuery(State: "active"), cancellationToken).Entries
            .Where(entry => string.Equals(entry.Source, source, StringComparison.Ordinal)
                && string.Equals(entry.Name, name, StringComparison.Ordinal))
            .Where(entry => string.IsNullOrWhiteSpace(origin)
                || string.Equals(entry.Origin, origin, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new KeyNotFoundException($"Startup entry not found: {source} / {name}");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException("Multiple startup entries matched; origin is required.");
        }

        var entry = matches[0];
        EnsureCanModify(entry.Source);
        var registryKind = IsShortcutSource(entry.Source) ? (RegistryValueKind?)null : CaptureRegistryKind(entry);
        var backups = (await ReadBackupsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var backupId = Guid.NewGuid().ToString("N");
        var backupPath = IsShortcutSource(entry.Source)
            ? Path.Combine(configuration.Settings.BackupDirectory, "startup", $"{backupId}.lnk")
            : null;
        var record = new StartupBackupRecord
        {
            Id = backupId,
            Action = "disable",
            Disposition = "disabled",
            DisabledAt = DateTimeOffset.Now,
            Source = entry.Source,
            Name = entry.Name,
            RawCommand = entry.RawCommand,
            RegistryKind = registryKind,
            Target = entry.Target,
            Arguments = entry.Arguments,
            WorkingDirectory = entry.WorkingDirectory,
            Origin = entry.Origin,
            BackupPath = backupPath,
        };
        using var shortcutPaths = OpenShortcutPaths(record, configuration.Settings.BackupDirectory, restoring: false);
        return await ExecuteMutationAsync("disable", record, backups, cancellationToken, shortcutPaths).ConfigureAwait(false);
    }

    public async Task<StartupMutationResult> RestoreAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await StartupMutationLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        EnsureNoUnresolvedTransaction();
        var backups = (await ReadBackupsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var matches = backups.Where(item => item is not null && string.Equals(item.Id, backupId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1) { throw new InvalidOperationException("The selected startup backup ID is ambiguous."); }
        var record = matches.FirstOrDefault() ?? throw new KeyNotFoundException($"Startup backup not found: {backupId}");
        if (record.EffectiveDisposition != "disabled")
        { throw new InvalidOperationException($"Startup backup is already {record.EffectiveDisposition}: {backupId}"); }

        EnsureCanModify(record.Source);
        if (!string.Equals(record.Action, "disable", StringComparison.Ordinal))
        { throw new InvalidOperationException("Only disabled startup backups can be restored."); }
        if (!IsShortcutSource(record.Source)) { _ = BackupRegistryKind(record); }
        var backupDirectory = IsShortcutSource(record.Source)
            ? (await _configurationLoader.LoadAsync(_paths.RootDirectory, cancellationToken).ConfigureAwait(false)).Settings.BackupDirectory
            : string.Empty;
        using var shortcutPaths = OpenShortcutPaths(record, backupDirectory, restoring: true);
        return await ExecuteMutationAsync("restore", record, backups, cancellationToken, shortcutPaths).ConfigureAwait(false);
    }

    private StartupShortcutPathLease? OpenShortcutPaths(StartupBackupRecord record, string backupDirectory, bool restoring) =>
        !IsShortcutSource(record.Source) ? null : StartupShortcutPathLease.Open(
            backupDirectory, record.Id, record.Name, record.Origin, record.BackupPath,
            _native.GetShortcutDirectory(record.Source),
            [_native.GetShortcutDirectory(UserStartupSource), _native.GetShortcutDirectory(CommonStartupSource)], restoring);

    private async Task<StartupMutationResult> ExecuteMutationAsync(
        string operation, StartupBackupRecord record, List<StartupBackupRecord> backups, CancellationToken cancellationToken,
        StartupShortcutPathLease? shortcutPaths)
    {
        // Prepare the exact bounded ledger image before ANY intent/native effect. Restore
        // uses a new record, so compensation never mutates or loses the original history.
        var completedRecord = operation == "disable" ? record : record with
            { Disposition = "restored", RestoredAt = DateTimeOffset.Now };
        var candidate = operation == "disable" ? backups.Append(completedRecord).ToArray()
            : backups.Select(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase) ? completedRecord : item).ToArray();
        var ledgerBytes = StartupBackupLedger.Write(candidate);
        var transactionBytes = JsonSerializer.SerializeToUtf8Bytes(new StartupTransaction(operation, record), JsonOptions);
        if (transactionBytes.Length > StartupBackupLedger.MaximumBytes)
        { throw new JsonException("Startup transaction exceeds the bounded input size."); }
        var intent = new PublicationProgress();
        try
        {
            await WriteBytesAtomicallyAsync(_transactionPath, transactionBytes,
                StartupMutationOperation.IntentPublication, intent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (intent.Started)
        {
            // Publication may have happened even when its call did not return.
            throw RecoveryRequired("intent_publication", false, failure);
        }

        var nativeStarted = false;
        void ApplyNative(Action action)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nativeStarted = true;
            _operations.Execute(StartupMutationOperation.NativeMutation, action);
        }
        try
        {
            if (operation == "disable") { DisableSystemEntry(record, ApplyNative, shortcutPaths); }
            else { RestoreSystemEntry(record, ApplyNative, shortcutPaths); }
        }
        catch (Exception failure)
        {
            if (nativeStarted) { throw RecoveryRequired("native_mutation", false, failure); }
            CleanupAfterDefiniteFailure(failure);
            throw;
        }

        var ledger = new PublicationProgress();
        try
        {
            await WriteBytesAtomicallyAsync(_paths.StartupBackupPath, ledgerBytes,
                StartupMutationOperation.LedgerPublication, ledger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // No blind compensation after an attempted namespace publication, even if
            // the exception is ACCESS_DENIED. It is not proof of the old ledger image.
            if (ledger.Started) { throw RecoveryRequired("ledger_publication", ledger.Returned, failure); }
            try
            {
                _operations.Execute(StartupMutationOperation.NativeRollback, () =>
                {
                    if (operation == "disable") { RollbackDisable(record); }
                    else { RollbackRestore(record); }
                });
            }
            catch (Exception rollbackFailure)
            {
                throw RecoveryRequired("native_rollback", false,
                    new AggregateException("Startup mutation failed and rollback did not complete.", failure, rollbackFailure));
            }
            CleanupAfterDefiniteFailure(failure);
            throw;
        }

        // The ledger and native call both returned. Intent cleanup is NOT a reason to
        // roll them back; a cleanup error may itself occur after deleting the intent.
        try { DeleteTransaction(); }
        catch (Exception failure) { throw RecoveryRequired("intent_cleanup", true, failure); }
        return new StartupMutationResult(true, operation, record.Source, record.Name, record.Id);

        StartupMutationRecoveryRequiredException RecoveryRequired(string stage, bool committed, Exception cause) =>
            new(operation, record.Id, stage, committed, cause);
        void CleanupAfterDefiniteFailure(Exception failure)
        {
            try { DeleteTransaction(); }
            catch (Exception cleanupFailure)
            {
                throw RecoveryRequired("intent_cleanup", false,
                    new AggregateException("Startup failure could not be finalized.", failure, cleanupFailure));
            }
        }
    }

    private static StartupEntry CreateActiveEntry(
        string source,
        string name,
        string? rawCommand,
        string? target,
        string? arguments,
        string? workingDirectory,
        string origin,
        string? error,
        IReadOnlyDictionary<string, string> managedTargets,
        bool elevated)
    {
        var requiresAdministrator = RequiresAdministrator(source);
        var managedTask = error is null && target is not null
            && managedTargets.TryGetValue(NormalizeTarget(target), out var taskName)
                ? taskName
                : null;
        return new StartupEntry(
            StableId(source, name, origin),
            source,
            name,
            rawCommand,
            target,
            arguments,
            workingDirectory,
            origin,
            error,
            false,
            null,
            null,
            requiresAdministrator,
            error is null && (!requiresAdministrator || elevated),
            managedTask);
    }

    private static StartupEntry ToDisabledEntry(StartupBackupRecord record)
    {
        var requiresAdministrator = RequiresAdministrator(record.Source);
        return new StartupEntry(
            $"backup:{record.Id}",
            record.Source,
            record.Name,
            record.RawCommand,
            record.Target,
            record.Arguments,
            record.WorkingDirectory,
            record.Origin,
            null,
            true,
            record.Id,
            record.DisabledAt,
            requiresAdministrator,
            false, // A disabled ledger record is metadata, never proof that restoration is currently available.
            null);
    }

    private RegistryValueKind CaptureRegistryKind(StartupEntry entry)
    {
        using var key = _native.OpenRunKey(entry.Source, writable: false, create: false)
            ?? throw new KeyNotFoundException($"Startup registry key is unavailable: {entry.Source}");
        if (!key.GetValueNames().Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
        { throw new KeyNotFoundException($"Startup registry value no longer exists: {entry.Name}"); }
        var kind = key.GetValueKind(entry.Name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        { throw new InvalidOperationException("Only String and ExpandString startup registry values can be backed up."); }
        var raw = key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (raw is null || !string.Equals(raw, entry.RawCommand, StringComparison.Ordinal)
            || key.GetValueKind(entry.Name) != kind)
        { throw new InvalidOperationException("Startup registry value changed while preparing its backup."); }
        return kind;
    }

    private static RegistryValueKind BackupRegistryKind(StartupBackupRecord record)
    {
        if (record.Source is not (HkcuRunSource or HklmRunSource or HklmWow64RunSource))
        { throw new InvalidOperationException("Unsupported startup registry backup source."); }
        // Missing/null is a legacy record, NOT evidence of the original type. Preserve the
        // old REG_SZ restore behavior; never claim recovered type fidelity for such a record.
        var kind = record.RegistryKind ?? RegistryValueKind.String;
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)
            || (record.RegistryKind.HasValue && record.RawCommand is null))
        { throw new InvalidOperationException("Unsupported or invalid startup registry backup value kind."); }
        return kind;
    }

    private static bool RegistryCommandMatches(RegistryKey key, StartupBackupRecord record)
    {
        var kind = BackupRegistryKind(record);
        return key.GetValueKind(record.Name) == kind
            && key.GetValue(record.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw
            && string.Equals(raw, record.RawCommand ?? string.Empty, StringComparison.Ordinal)
            && key.GetValueKind(record.Name) == kind;
    }

    private void DisableSystemEntry(StartupBackupRecord record, Action<Action> apply, StartupShortcutPathLease? shortcutPaths)
    {
        if (IsShortcutSource(record.Source))
        {
            var paths = shortcutPaths ?? throw new InvalidOperationException("Shortcut path lease is required.");
            _operations.Execute(StartupMutationOperation.ShortcutPreparation, () =>
            {
                paths.EnsureArchiveDirectory();
                paths.CheckState(restoring: false);
            });
            apply(() => File.Move(paths.Origin, paths.BackupPath, overwrite: false));
            return;
        }

        using var key = _native.OpenRunKey(record.Source, writable: true, create: false)
            ?? throw new KeyNotFoundException($"Startup registry key is unavailable: {record.Source}");
        if (!key.GetValueNames().Contains(record.Name, StringComparer.OrdinalIgnoreCase))
        {
            throw new KeyNotFoundException($"Startup registry value no longer exists: {record.Name}");
        }

        // Best-effort revalidation, not an atomic CAS against non-cooperating native writers.
        if (!RegistryCommandMatches(key, record))
        { throw new InvalidOperationException("Startup registry value changed before disable."); }
        apply(() => key.DeleteValue(record.Name, throwOnMissingValue: true));
    }

    private void RestoreSystemEntry(StartupBackupRecord record, Action<Action> apply, StartupShortcutPathLease? shortcutPaths)
    {
        if (IsShortcutSource(record.Source))
        {
            var paths = shortcutPaths ?? throw new InvalidOperationException("Shortcut path lease is required.");
            _operations.Execute(StartupMutationOperation.ShortcutPreparation, () =>
            {
                paths.EnsureOriginDirectory();
                paths.CheckState(restoring: true);
            });
            apply(() => File.Move(paths.BackupPath, paths.Origin, overwrite: false));
            return;
        }

        var kind = BackupRegistryKind(record);
        using var key = _native.OpenRunKey(record.Source, writable: true, create: false);
        if (key is not null)
        {
            if (key.GetValueNames().Contains(record.Name, StringComparer.OrdinalIgnoreCase))
            { throw new IOException($"Restore registry value already exists: {record.Name}"); }
            apply(() => key.SetValue(record.Name, record.RawCommand ?? string.Empty, kind));
            return;
        }
        // Creation is itself a native effect. Include it in the unknown-outcome boundary;
        // recreating this fixed key does not recover other externally deleted values.
        apply(() =>
        {
            using var created = _native.OpenRunKey(record.Source, writable: true, create: true)
                ?? throw new InvalidOperationException($"Unable to open startup registry key: {record.Source}");
            if (created.GetValueNames().Contains(record.Name, StringComparer.OrdinalIgnoreCase))
            { throw new IOException($"Restore registry value already exists: {record.Name}"); }
            created.SetValue(record.Name, record.RawCommand ?? string.Empty, kind);
        });
    }

    private void RollbackDisable(StartupBackupRecord record)
    {
        if (IsShortcutSource(record.Source))
        {
            if (string.IsNullOrWhiteSpace(record.BackupPath)
                || !File.Exists(record.BackupPath) || File.Exists(record.Origin))
            { throw new IOException("Startup shortcut state changed during disable rollback."); }
            File.Move(record.BackupPath, record.Origin, overwrite: false);
            return;
        }

        using var key = _native.OpenRunKey(record.Source, writable: true, create: true)
            ?? throw new InvalidOperationException("Unable to roll back startup registry mutation.");
        if (!key.GetValueNames().Contains(record.Name, StringComparer.OrdinalIgnoreCase))
        {
            key.SetValue(record.Name, record.RawCommand ?? string.Empty, BackupRegistryKind(record));
        }
        else if (!RegistryCommandMatches(key, record))
        {
            // A foreign value must not be overwritten or silently counted as our rollback.
            throw new IOException("Startup registry value changed during disable rollback.");
        }
    }

    private void RollbackRestore(StartupBackupRecord record)
    {
        if (IsShortcutSource(record.Source))
        {
            if (!File.Exists(record.Origin) || string.IsNullOrWhiteSpace(record.BackupPath))
            { throw new IOException("Startup shortcut state changed during restore rollback."); }
            Directory.CreateDirectory(Path.GetDirectoryName(record.BackupPath)!);
            File.Move(record.Origin, record.BackupPath, overwrite: false);
            return;
        }

        using var key = _native.OpenRunKey(record.Source, writable: true, create: false);
        if (key is null || !key.GetValueNames().Contains(record.Name, StringComparer.OrdinalIgnoreCase)) { return; }
        if (!RegistryCommandMatches(key, record))
        { throw new IOException("Startup registry value changed during restore rollback."); }
        key.DeleteValue(record.Name, throwOnMissingValue: true);
    }

    private Task<IReadOnlyList<StartupBackupRecord>> ReadBackupsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ledger = ReadBackupLedger(requireComplete: true);
        if (ledger.Truncated || ledger.InvalidRecords)
        { throw new InvalidOperationException("Startup backup ledger is incomplete, invalid or ambiguous; no mutation is allowed."); }
        return Task.FromResult(ledger.Records);
    }

    private StartupBackupLedgerRead ReadBackupLedger(bool requireComplete = false)
    {
        // Pin only this fixed root input while reading; never follow editable record paths.
        // Release before publication. This is not a ledger CAS against external writers.
        using var pin = SshInputFileLease.OpenOptional(_paths.StartupBackupPath, StartupBackupLedger.MaximumBytes);
        if (pin is null) { return new([], false, false); }
        using var stream = new FileStream(_paths.StartupBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > StartupBackupLedger.MaximumBytes) { throw new IOException("Startup backup exceeds the bounded read size."); }
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return StartupBackupLedger.Read(bytes, requireComplete);
    }

    private sealed class PublicationProgress
    {
        internal bool Started { get; set; }
        internal bool Returned { get; set; }
    }

    private async Task WriteBytesAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes,
        StartupMutationOperation operation, PublicationProgress progress, CancellationToken cancellationToken, Action? beforePublish = null)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Invalid JSON path: {path}");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        Exception? failure = null;
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Once publication is invoked, an exception alone cannot establish which
            // image is on disk. No error-code guessing or automatic publish retry.
            cancellationToken.ThrowIfCancellationRequested();
            progress.Started = true;
            _operations.Execute(operation, () => { beforePublish?.Invoke(); File.Move(tempPath, path, overwrite: true); });
            progress.Returned = true;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { _operations.Execute(StartupMutationOperation.TemporaryCleanup, () => File.Delete(tempPath)); }
            catch (Exception cleanupFailure) when (failure is not null)
            { throw new AggregateException("Startup JSON write and candidate cleanup both failed.", failure, cleanupFailure); }
        }
    }

    private void EnsureNoUnresolvedTransaction()
    {
        try { _ = File.GetAttributes(_transactionPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        // Also refuse directories/reparse leaves and propagate denied/unknown probes.
        throw new InvalidOperationException($"An unresolved startup transaction exists: {_transactionPath}");
    }

    private void DeleteTransaction() =>
        _operations.Execute(StartupMutationOperation.IntentCleanup, () => File.Delete(_transactionPath));

    private static object CreateWshShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new PlatformNotSupportedException("WScript.Shell COM is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Failed to create WScript.Shell.");
    }

    internal static void KeepComObjectAlive(object? value)
    {
        // RCW import counts do not count managed/DLR aliases. A balanced manual
        // release can still disconnect another user. The CLR owns reclamation;
        // retain our reference through the last native use, never force release.
        GC.KeepAlive(value);
    }

    private static bool IsShortcutSource(string source) =>
        source is UserStartupSource or CommonStartupSource;

    private static bool RequiresAdministrator(string source) =>
        source is CommonStartupSource or HklmRunSource or HklmWow64RunSource;

    private void EnsureCanModify(string source)
    {
        if (RequiresAdministrator(source) && !_native.IsAdministrator)
        {
            throw new UnauthorizedAccessException($"Startup source '{source}' requires administrator privileges.");
        }
    }

    private static string NormalizeTarget(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return expanded;
        }
    }

    private static string StableId(string source, string name, string origin)
    {
        var data = Encoding.UTF8.GetBytes($"{source}\0{name}\0{origin}".ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private sealed record StartupTransaction(
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("record")] StartupBackupRecord Record);

    // Public IDisposable is retained for existing callers. All handles are operation-scoped.
    public void Dispose() { }
}
