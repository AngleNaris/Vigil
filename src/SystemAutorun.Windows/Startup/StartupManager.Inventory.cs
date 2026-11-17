using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

public sealed partial class StartupManager
{
    // Shared by the GUI compatibility snapshot, agent inventory and native write preflight.
    // Metadata only: no target existence checks, shell resolution, backup restoration probes,
    // filesystem writes, Run(), Save() or elevation. This is not native write authority.
    internal StartupManagementSnapshot CaptureInventory(AutorunConfiguration configuration, StartupListQuery query,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? shortcutDirectories = null)
    {
        query = query.Validate();
        var elevated = _native.IsAdministrator; var entries = new List<StartupEntry>();
        var issues = new Dictionary<string, string>(StringComparer.Ordinal); var readable = 0; var truncated = false;
        var managed = configuration.Tasks.Where(task => task.Type == AutorunTaskType.Windows && !string.IsNullOrWhiteSpace(task.Executable))
            .GroupBy(task => NormalizeTarget(task.Executable!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        object? shell = null; var shellProbed = false;
        try
        {
            if (query.State != "disabled")
            {
                Folder(UserStartupSource);
                Folder(CommonStartupSource);
                Registry(HkcuRunSource); Registry(HklmRunSource); Registry(HklmWow64RunSource);
            }
            if (query.State != "active") { Probe("backups", ReadDisabled); }
            cancellationToken.ThrowIfCancellationRequested();
            return new(entries.ToArray(), entries.Count(entry => !entry.IsDisabled),
                entries.Count(entry => !entry.IsDisabled && entry.IsManaged), entries.Count(entry => entry.IsDisabled), elevated)
            { Truncated = truncated, ReadableSources = readable, SourceIssues = issues.Select(pair => new StartupSourceIssue(pair.Key, pair.Value)).ToArray() };
        }
        finally { KeepComObjectAlive(shell); }

        void Probe(string source, Action read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { read(); if (!issues.ContainsKey(source)) { readable++; } }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or COMException or TypeLoadException or InvalidCastException
                or PlatformNotSupportedException or System.Security.SecurityException or JsonException)
            {
                issues.TryAdd(source, exception is UnauthorizedAccessException or System.Security.SecurityException
                    || exception.InnerException is Win32Exception { NativeErrorCode: 5 } ? "permission_denied"
                    : exception is JsonException ? "backup_invalid" : "source_unavailable");
            }
        }

        void AddEntry(StartupEntry entry, string source)
        {
            // The compatibility GUI does not pass through the agent's response validator.
            // Reject malformed/oversized metadata here as well, without probing any path.
            if (!StartupListQuery.ValidEntry(entry))
            {
                issues.TryAdd(source, source == "backups" ? "backup_record_invalid" : "entry_invalid");
                truncated = true;
                return;
            }
            entries.Add(entry);
        }

        void Folder(string source)
        {
            if (!query.IncludesSource(source)) { return; }
            Probe(source, () =>
            {
                // Tests may supply an owned local folder internally; RPC never accepts paths.
                var directory = shortcutDirectories?.GetValueOrDefault(source) ?? _native.GetShortcutDirectory(source);
                SshInputFileLease directoryPin;
                try { directoryPin = SshInputFileLease.OpenDirectory(directory); }
                catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
                { return; } // Only the initial missing folder is an observed empty source.
                using var pinnedDirectory = directoryPin;
                var count = 0;
                foreach (var path in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (count++ == 512) { truncated = true; break; }
                    using var filePin = SshInputFileLease.Open(path, 64 * 1024);
                    if (!shellProbed)
                    {
                        shellProbed = true;
                        try { shell = CreateWshShell(); }
                        catch (Exception exception) when (exception is COMException or TypeLoadException or InvalidOperationException or PlatformNotSupportedException)
                        { shell = null; }
                    }
                    if (shell is null) { issues.TryAdd(source, "shortcut_provider_unavailable"); return; }
                    object? shortcutObject = null;
                    try
                    {
                        shortcutObject = StartupComDispatch.Call(shell, "CreateShortcut", path);
                        var target = StartupComDispatch.Text(shortcutObject, "TargetPath");
                        AddEntry(CreateActiveEntry(source, Path.GetFileNameWithoutExtension(path), target, target,
                            StartupComDispatch.Text(shortcutObject, "Arguments"), StartupComDispatch.Text(shortcutObject, "WorkingDirectory"),
                            path, null, managed, elevated), source);
                    }
                    catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidOperationException)
                    {
                        AddEntry(CreateActiveEntry(source, Path.GetFileNameWithoutExtension(path), null, null, null,
                            null, path, "shortcut_read_failed", managed, elevated), source);
                    }
                    finally { KeepComObjectAlive(shortcutObject); }
                }
            });
        }

        void Registry(string source)
        {
            if (!query.IncludesSource(source)) { return; }
            Probe(source, () =>
            {
                using var key = _native.OpenRunKey(source, writable: false, create: false);
                if (key is null) { return; }
                // Do not allocate an unbounded GetValueNames array for an already oversized key.
                if (key.ValueCount > 512) { truncated = true; issues.TryAdd(source, "registry_source_limit"); return; }
                var count = 0;
                foreach (var name in key.GetValueNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (count++ == 512) { truncated = true; break; }
                    var kind = key.GetValueKind(name);
                    var raw = kind is RegistryValueKind.String or RegistryValueKind.ExpandString
                        ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string : null;
                    var error = kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) ? "registry_value_type_unsupported" : null;
                    if (error is null && (raw is null || key.GetValueKind(name) != kind))
                    { raw = null; error = "registry_value_changed"; } // Best-effort type consistency, not native CAS.
                    if (raw?.Length > 32768) { raw = null; error = "registry_value_too_large"; }
                    var parsed = SplitInventoryCommand(raw);
                    if (error is null && !string.IsNullOrWhiteSpace(raw) && parsed.Target is null)
                    { error = "registry_command_ambiguous"; }
                    var hive = source == HkcuRunSource ? "HKCU" : "HKLM";
                    var suffix = source == HklmWow64RunSource ? @"\WOW6432Node" : "";
                    var origin = $@"{hive}:\Software{suffix}\Microsoft\Windows\CurrentVersion\Run :: {name}";
                    AddEntry(CreateActiveEntry(source, name, raw, parsed.Target, parsed.Arguments, null,
                        origin, error, managed, elevated), source);
                }
            });
        }

        void ReadDisabled()
        {
            // Both files are pinned local inputs; never inspect any backup's target or backup_path.
            using var transaction = SshInputFileLease.OpenOptional(_transactionPath, 512 * 1024);
            if (transaction is not null) { issues.TryAdd("backups", "startup_transaction_unresolved"); }
            var ledger = ReadBackupLedger();
            if (ledger.Truncated) { truncated = true; }
            if (ledger.InvalidRecords) { issues.TryAdd("backups", "backup_record_invalid"); truncated = true; }
            foreach (var record in ledger.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.EffectiveDisposition == "disabled" && query.IncludesSource(record.Source))
                { AddEntry(ToDisabledEntry(record), "backups"); } // Restorability is deliberately not probed.
            }
        }
    }

    // Deliberately lexical. Windows permits ambiguous unquoted commands; no File.Exists,
    // environment expansion, executable search, network lookup or command execution is used.
    internal static (string? Target, string Arguments) SplitInventoryCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) { return (null, ""); }
        var value = command.Trim();
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end > 1 && (end + 1 == value.Length || char.IsWhiteSpace(value[end + 1]))
                ? (value[1..end], value[(end + 1)..].Trim()) : (null, value);
        }
        var separator = value.IndexOfAny([' ', '\t']);
        if (separator < 0) { return value.Contains('"') ? (null, value) : (value, ""); }
        var first = value[..separator];
        if (first.Contains('"')) { return (null, value); }
        // A space inside an unquoted path must not become a guessed C:\Program identity.
        // Without an explicit filename extension even the first token may require native
        // executable search. Keep the raw command for review rather than doing that search.
        return Path.HasExtension(first) ? (first, value[(separator + 1)..].Trim()) : (null, value);
    }
}
