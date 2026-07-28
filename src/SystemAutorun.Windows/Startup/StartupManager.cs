using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

public sealed class StartupManager : IDisposable
{
    public const string UserStartupSource = "Startup";
    public const string CommonStartupSource = "Common Startup";
    public const string HkcuRunSource = "HKCU";
    public const string HklmRunSource = "HKLM";
    public const string HklmWow64RunSource = "HKLM-WOW64";

    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ProjectPaths _paths;
    private readonly ConfigurationLoader _configurationLoader;
    private readonly string _transactionPath;
    private readonly Mutex _mutationMutex;

    public StartupManager(ProjectPaths paths, ConfigurationLoader? configurationLoader = null)
    {
        _paths = paths;
        _configurationLoader = configurationLoader ?? new ConfigurationLoader();
        _transactionPath = paths.StartupBackupPath + ".transaction";
        var mutexName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.RootDirectory)))[..20];
        _mutationMutex = new Mutex(false, $"Local\\SystemAutorun.Startup.{mutexName}");
    }

    public async Task<StartupManagementSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationLoader.LoadAsync(_paths.RootDirectory, cancellationToken)
            .ConfigureAwait(false);
        var elevated = IsAdministrator();
        var active = ScanActiveEntries(configuration.Tasks, elevated);
        var backups = await ReadBackupsAsync(cancellationToken).ConfigureAwait(false);
        var disabled = backups
            .Where(record => record.RestoredAt is null)
            .Select(record => ToDisabledEntry(record, elevated))
            .ToArray();
        return new StartupManagementSnapshot(
            active.Concat(disabled).ToArray(),
            active.Count,
            active.Count(entry => entry.IsManaged),
            disabled.Length,
            elevated);
    }

    public async Task<StartupMutationResult> DisableAsync(
        string source,
        string name,
        string? origin,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireMutationLock();
        EnsureNoUnresolvedTransaction();
        var configuration = await _configurationLoader.LoadAsync(_paths.RootDirectory, cancellationToken)
            .ConfigureAwait(false);
        var matches = ScanActiveEntries(configuration.Tasks, IsAdministrator())
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
        var backups = (await ReadBackupsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var backupId = Guid.NewGuid().ToString("N");
        var backupPath = IsShortcutSource(entry.Source)
            ? Path.Combine(configuration.Settings.BackupDirectory, "startup", $"{backupId}.lnk")
            : null;
        var record = new StartupBackupRecord
        {
            Id = backupId,
            Action = "disable",
            DisabledAt = DateTimeOffset.Now,
            Source = entry.Source,
            Name = entry.Name,
            RawCommand = entry.RawCommand,
            Target = entry.Target,
            Arguments = entry.Arguments,
            WorkingDirectory = entry.WorkingDirectory,
            Origin = entry.Origin,
            BackupPath = backupPath,
        };
        await WriteTransactionAsync(new StartupTransaction("disable", record), cancellationToken)
            .ConfigureAwait(false);

        var mutationApplied = false;
        try
        {
            DisableSystemEntry(record);
            mutationApplied = true;
            backups.Add(record);
            await WriteBackupsAsync(backups, cancellationToken).ConfigureAwait(false);
            DeleteTransaction();
            return new StartupMutationResult(true, "disable", entry.Source, entry.Name, backupId);
        }
        catch
        {
            if (mutationApplied)
            {
                RollbackDisable(record);
            }

            DeleteTransaction();
            throw;
        }
    }

    public async Task<StartupMutationResult> RestoreAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireMutationLock();
        EnsureNoUnresolvedTransaction();
        var backups = (await ReadBackupsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var record = backups.FirstOrDefault(item =>
            string.Equals(item.Id, backupId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Startup backup not found: {backupId}");
        if (record.RestoredAt is not null)
        {
            throw new InvalidOperationException($"Startup backup is already restored: {backupId}");
        }

        EnsureCanModify(record.Source);
        await WriteTransactionAsync(new StartupTransaction("restore", record), cancellationToken)
            .ConfigureAwait(false);
        var mutationApplied = false;
        try
        {
            RestoreSystemEntry(record);
            mutationApplied = true;
            record.RestoredAt = DateTimeOffset.Now;
            await WriteBackupsAsync(backups, cancellationToken).ConfigureAwait(false);
            DeleteTransaction();
            return new StartupMutationResult(true, "restore", record.Source, record.Name, record.Id);
        }
        catch
        {
            if (mutationApplied)
            {
                record.RestoredAt = null;
                RollbackRestore(record);
            }

            DeleteTransaction();
            throw;
        }
    }

    internal static (string? Target, string Arguments) SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (null, string.Empty);
        }

        var value = command.Trim();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            value,
            @"\.(?:exe|com|bat|cmd|ps1|vbs)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return (value[1..end], value[(end + 1)..].Trim());
            }
        }

        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var end = matches[index].Index + matches[index].Length;
            var candidate = value[..end].Trim();
            if (File.Exists(Environment.ExpandEnvironmentVariables(candidate)))
            {
                return (candidate, value[end..].Trim());
            }
        }

        if (matches.Count > 0)
        {
            var end = matches[0].Index + matches[0].Length;
            return (value[..end].Trim(), value[end..].Trim());
        }

        var separator = value.IndexOf(' ');
        return separator < 0
            ? (value, string.Empty)
            : (value[..separator], value[(separator + 1)..].Trim());
    }

    private IReadOnlyList<StartupEntry> ScanActiveEntries(
        IReadOnlyList<AutorunTask> tasks,
        bool elevated)
    {
        var managedTargets = tasks
            .Where(task => task.Type == AutorunTaskType.Windows && !string.IsNullOrWhiteSpace(task.Executable))
            .GroupBy(task => NormalizeTarget(task.Executable!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        var entries = new List<StartupEntry>();
        ScanShortcutFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            UserStartupSource,
            managedTargets,
            elevated,
            entries);
        ScanShortcutFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            CommonStartupSource,
            managedTargets,
            elevated,
            entries);
        ScanRunKey(RegistryHive.CurrentUser, RegistryView.Default, HkcuRunSource, managedTargets, elevated, entries);
        ScanRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, HklmRunSource, managedTargets, elevated, entries);
        ScanRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, HklmWow64RunSource, managedTargets, elevated, entries);
        return entries;
    }

    private static void ScanShortcutFolder(
        string directory,
        string source,
        IReadOnlyDictionary<string, string> managedTargets,
        bool elevated,
        ICollection<StartupEntry> entries)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            string? target = null;
            string? arguments = null;
            string? workingDirectory = null;
            string? error = null;
            try
            {
                dynamic shortcut = CreateWshShell().CreateShortcut(path);
                target = shortcut.TargetPath as string;
                arguments = shortcut.Arguments as string;
                workingDirectory = shortcut.WorkingDirectory as string;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException)
            {
                error = exception.Message;
            }

            entries.Add(CreateActiveEntry(
                source,
                name,
                target,
                target,
                arguments,
                workingDirectory,
                path,
                error,
                managedTargets,
                elevated));
        }
    }

    private static void ScanRunKey(
        RegistryHive hive,
        RegistryView view,
        string source,
        IReadOnlyDictionary<string, string> managedTargets,
        bool elevated,
        ICollection<StartupEntry> entries)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(RunSubKey, writable: false);
        if (key is null)
        {
            return;
        }

        foreach (var name in key.GetValueNames())
        {
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            var parsed = SplitCommand(raw);
            var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            var viewSuffix = view == RegistryView.Registry32 ? @"\WOW6432Node" : string.Empty;
            var origin = $@"{hiveName}:\Software{viewSuffix}\Microsoft\Windows\CurrentVersion\Run :: {name}";
            entries.Add(CreateActiveEntry(
                source,
                name,
                raw,
                parsed.Target,
                parsed.Arguments,
                null,
                origin,
                null,
                managedTargets,
                elevated));
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
        var managedTask = target is not null
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
            !requiresAdministrator || elevated,
            managedTask);
    }

    private static StartupEntry ToDisabledEntry(StartupBackupRecord record, bool elevated)
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
            !requiresAdministrator || elevated,
            null);
    }

    private void DisableSystemEntry(StartupBackupRecord record)
    {
        if (IsShortcutSource(record.Source))
        {
            ValidateShortcutOrigin(record.Source, record.Origin);
            if (!File.Exists(record.Origin) || string.IsNullOrWhiteSpace(record.BackupPath))
            {
                throw new FileNotFoundException("Startup shortcut no longer exists.", record.Origin);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(record.BackupPath)!);
            File.Move(record.Origin, record.BackupPath, overwrite: false);
            return;
        }

        using var key = OpenRunKey(record.Source, writable: true, create: false)
            ?? throw new KeyNotFoundException($"Startup registry key is unavailable: {record.Source}");
        if (!key.GetValueNames().Contains(record.Name, StringComparer.Ordinal))
        {
            throw new KeyNotFoundException($"Startup registry value no longer exists: {record.Name}");
        }

        key.DeleteValue(record.Name, throwOnMissingValue: true);
    }

    private static void RestoreSystemEntry(StartupBackupRecord record)
    {
        if (IsShortcutSource(record.Source))
        {
            ValidateShortcutOrigin(record.Source, record.Origin);
            if (File.Exists(record.Origin))
            {
                throw new IOException($"Restore target already exists: {record.Origin}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(record.Origin)!);
            if (!string.IsNullOrWhiteSpace(record.BackupPath) && File.Exists(record.BackupPath))
            {
                File.Move(record.BackupPath, record.Origin, overwrite: false);
            }
            else
            {
                CreateShortcut(record);
            }

            return;
        }

        using var key = OpenRunKey(record.Source, writable: true, create: true)
            ?? throw new InvalidOperationException($"Unable to open startup registry key: {record.Source}");
        if (key.GetValue(record.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
        {
            throw new IOException($"Restore registry value already exists: {record.Name}");
        }

        key.SetValue(record.Name, record.RawCommand ?? string.Empty, RegistryValueKind.String);
    }

    private static void RollbackDisable(StartupBackupRecord record)
    {
        if (IsShortcutSource(record.Source))
        {
            if (!string.IsNullOrWhiteSpace(record.BackupPath)
                && File.Exists(record.BackupPath)
                && !File.Exists(record.Origin))
            {
                File.Move(record.BackupPath, record.Origin, overwrite: false);
            }

            return;
        }

        using var key = OpenRunKey(record.Source, writable: true, create: true)
            ?? throw new InvalidOperationException("Unable to roll back startup registry mutation.");
        if (key.GetValue(record.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null)
        {
            key.SetValue(record.Name, record.RawCommand ?? string.Empty, RegistryValueKind.String);
        }
    }

    private static void RollbackRestore(StartupBackupRecord record)
    {
        if (IsShortcutSource(record.Source))
        {
            if (File.Exists(record.Origin) && !string.IsNullOrWhiteSpace(record.BackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(record.BackupPath)!);
                File.Move(record.Origin, record.BackupPath, overwrite: false);
            }

            return;
        }

        using var key = OpenRunKey(record.Source, writable: true, create: false);
        var current = key?.GetValue(record.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        if (string.Equals(current, record.RawCommand ?? string.Empty, StringComparison.Ordinal))
        {
            key!.DeleteValue(record.Name, throwOnMissingValue: false);
        }
    }

    private async Task<IReadOnlyList<StartupBackupRecord>> ReadBackupsAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.StartupBackupPath))
        {
            return Array.Empty<StartupBackupRecord>();
        }

        await using var stream = File.OpenRead(_paths.StartupBackupPath);
        var records = await JsonSerializer.DeserializeAsync<List<StartupBackupRecord>>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return records ?? [];
    }

    private Task WriteBackupsAsync(
        IReadOnlyList<StartupBackupRecord> backups,
        CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(_paths.StartupBackupPath, backups, cancellationToken);

    private Task WriteTransactionAsync(
        StartupTransaction transaction,
        CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(_transactionPath, transaction, cancellationToken);

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Invalid JSON path: {path}");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private IDisposable AcquireMutationLock()
    {
        if (!_mutationMutex.WaitOne(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("Another startup-item operation is still running.");
        }

        return new MutexLease(_mutationMutex);
    }

    private void EnsureNoUnresolvedTransaction()
    {
        if (File.Exists(_transactionPath))
        {
            throw new InvalidOperationException(
                $"An unresolved startup transaction exists: {_transactionPath}");
        }
    }

    private void DeleteTransaction()
    {
        if (File.Exists(_transactionPath))
        {
            File.Delete(_transactionPath);
        }
    }

    private static dynamic CreateWshShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new PlatformNotSupportedException("WScript.Shell COM is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Failed to create WScript.Shell.");
    }

    private static void CreateShortcut(StartupBackupRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Target))
        {
            throw new InvalidOperationException("The shortcut backup has no target path.");
        }

        dynamic shortcut = CreateWshShell().CreateShortcut(record.Origin);
        shortcut.TargetPath = record.Target;
        shortcut.Arguments = record.Arguments ?? string.Empty;
        shortcut.WorkingDirectory = record.WorkingDirectory ?? string.Empty;
        shortcut.Save();
    }

    private static RegistryKey? OpenRunKey(string source, bool writable, bool create)
    {
        var (hive, view) = source switch
        {
            HkcuRunSource => (RegistryHive.CurrentUser, RegistryView.Default),
            HklmRunSource => (RegistryHive.LocalMachine, RegistryView.Registry64),
            HklmWow64RunSource => (RegistryHive.LocalMachine, RegistryView.Registry32),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported startup source."),
        };
        var baseKey = RegistryKey.OpenBaseKey(hive, view);
        var key = create
            ? baseKey.CreateSubKey(RunSubKey, writable)
            : baseKey.OpenSubKey(RunSubKey, writable);
        baseKey.Dispose();
        return key;
    }

    private static void ValidateShortcutOrigin(string source, string origin)
    {
        var expectedDirectory = source switch
        {
            UserStartupSource => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            CommonStartupSource => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported shortcut source."),
        };
        var fullOrigin = Path.GetFullPath(origin);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory));
        if (!string.Equals(Path.GetDirectoryName(fullOrigin), fullDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullOrigin), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Shortcut origin is outside the expected Startup directory.");
        }
    }

    private static bool IsShortcutSource(string source) =>
        source is UserStartupSource or CommonStartupSource;

    private static bool RequiresAdministrator(string source) =>
        source is CommonStartupSource or HklmRunSource or HklmWow64RunSource;

    private static void EnsureCanModify(string source)
    {
        if (RequiresAdministrator(source) && !IsAdministrator())
        {
            throw new UnauthorizedAccessException($"Startup source '{source}' requires administrator privileges.");
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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

    private sealed class StartupBackupRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; init; } = "disable";

        [JsonPropertyName("disabled_at")]
        public DateTimeOffset DisabledAt { get; init; }

        [JsonPropertyName("restored_at")]
        public DateTimeOffset? RestoredAt { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("raw_command")]
        public string? RawCommand { get; init; }

        [JsonPropertyName("target")]
        public string? Target { get; init; }

        [JsonPropertyName("args")]
        public string? Arguments { get; init; }

        [JsonPropertyName("working_dir")]
        public string? WorkingDirectory { get; init; }

        [JsonPropertyName("origin")]
        public string Origin { get; init; } = string.Empty;

        [JsonPropertyName("backup_path")]
        public string? BackupPath { get; init; }
    }

    private sealed class MutexLease : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        internal MutexLease(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _mutex.ReleaseMutex();
        }
    }

    public void Dispose() => _mutationMutex.Dispose();
}
