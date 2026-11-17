using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal sealed record StartupHistoryNativeCheck(string State, string Reason, DateTimeOffset? ObservedAt = null)
{
    public bool NativeWriteReady => false;
    public bool ExecutionRequested => false;
    public bool TransactionFinalized => false;
    public bool MayFinalize => false;
}

// A separate axis; the R44 metadata observation remains native-unobserved.
internal sealed record StartupHistoryReconciliationObservation(StartupHistoryIntentObservation Metadata, StartupHistoryNativeCheck Native);

internal sealed partial class StartupHistoryIntentReader
{
    internal async Task<StartupHistoryReconciliationObservation> InspectNativeAsync(IStartupNativeAccess native,
        CancellationToken token = default, Action<string>? checkpoint = null)
    {
        var metadata = Inspect(token); var unobserved = new StartupHistoryNativeCheck("unobserved", "history_native_inputs_unavailable");
        if (!NativeEligible(metadata)) { return new(metadata, unobserved); }
        try
        {
            using var root = SshInputFileLease.OpenDirectory(_paths.RootDirectory);
            using var mutation = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, StartupMutationLease.FileName), 0);
            using var workspace = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, RootWriteLease.FileName), 0);
            if (mutation is null || workspace is null) { return new(metadata, unobserved with { Reason = "history_query_coordination_missing" }); }
            using var configuration = RootImage.Open(_paths.ConfigPath, ConfigurationDocumentRequest.MaximumBytes);
            if (configuration.Bytes is null) { return new(metadata, unobserved with { Reason = "history_native_configuration_missing" }); }
            // Parse only this held snapshot. The general loader also opens overrides,
            // which are irrelevant here and could be a late/reparse optional input.
            using var text = new StreamReader(new MemoryStream(configuration.Bytes, writable: false));
            var yaml = await text.ReadToEndAsync(token).ConfigureAwait(false); var loader = new ConfigurationLoader();
            var current = loader.ReadSettingsText(yaml, _paths.RootDirectory, token);
            var archiveBase = StartupShortcutPathLease.LocalPath(current.BackupDirectory);
            checkpoint?.Invoke("native_configuration_loaded"); token.ThrowIfCancellationRequested();
            // Disposed before configuration/coordination pins. Native objects stay held
            // through the protected reader's seal AND the root metadata final checks.
            using var reconciliation = new StartupHistoryNativeReconciler(_paths.RootDirectory, archiveBase, native, token, checkpoint);
            metadata = InspectCore(token, checkpoint, reconciliation.Observe);
            var after = loader.ReadSettingsText(yaml, _paths.RootDirectory, token);
            if (StartupShortcutPathLease.LocalPath(after.BackupDirectory) != archiveBase)
            { return new(metadata, new("unverified", "history_native_configuration_changed")); }
            configuration.VerifyCurrent(); token.ThrowIfCancellationRequested();
            return new(metadata, NativeEligible(metadata) ? reconciliation.InspectCurrent() : new("unverified", "history_native_join_unverified"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ConfigurationException or IOException or UnauthorizedAccessException or SecurityException or Win32Exception
            or InvalidOperationException or ArgumentException or CryptographicException or JsonException)
        { return new(metadata, new("unverified", "history_native_query_unavailable")); }
    }

    private static bool NativeEligible(StartupHistoryIntentObservation metadata) => metadata.State == "observed_pending"
        && metadata.ConfigurationImage == "matched_image" && metadata.LedgerImage is "declared_empty" or "previous_image" or "proposed_image"
        && metadata.HistoryState is "candidate" or "protected_committed";
}

internal sealed class StartupHistoryNativeReconciler(string projectRoot, string archiveBase, IStartupNativeAccess native,
    CancellationToken token, Action<string>? checkpoint = null) : IDisposable
{
    private StartupNativeSlotProbe.HistoryNativeReadLease? _slots;
    private StartupHistoryBinding? _binding;
    private StartupHistoryNativeCheck _observation = new("unobserved", "history_native_inputs_unavailable");
    private bool _observed;
    private bool _disposed;

    // Only the external authenticated reader calls this, while index/proofs are held.
    internal void Observe(StartupHistoryPayload payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_observed) { throw new InvalidOperationException("Only one authenticated native observation is accepted."); }
        _observed = true;
        try
        {
            token.ThrowIfCancellationRequested(); _binding = payload.Binding;
            if (StartupShortcutPathLease.LocalPath(projectRoot) != _binding.Root.Path || archiveBase != _binding.ArchiveBase.Path)
            { _observation = new("unverified", "history_native_configuration_changed"); return; }
            var scope = StartupSourceScopeProbe.Observe(_binding.Source.Source, native, token);
            if (!Matches(scope, _binding)) { _observation = new("unverified", "history_native_source_changed"); return; }
            _slots = StartupNativeSlotProbe.HistoryNativeReadLease.Open(_binding, projectRoot, scope.DirectoryPath!, archiveBase,
                [native.GetShortcutDirectory(StartupManager.UserStartupSource), native.GetShortcutDirectory(StartupManager.CommonStartupSource)], token);
            checkpoint?.Invoke("native_slots_read"); token.ThrowIfCancellationRequested(); _observation = InspectCurrent();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception
            or InvalidOperationException or ArgumentException or CryptographicException or JsonException)
        { _observation = new("unverified", "history_native_slots_unavailable"); }
    }

    internal StartupHistoryNativeCheck InspectCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        if (_slots is null || _observation.State == "unverified") { return _observation; }
        try
        {
            _slots.VerifyCurrent(token); var scope = StartupSourceScopeProbe.Observe(_binding!.Source.Source, native, token);
            if (!Matches(scope, _binding)) { return new("unverified", "history_native_source_changed"); }
            _slots.VerifyCurrent(token); token.ThrowIfCancellationRequested(); return _slots.Compare();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception
            or InvalidOperationException or ArgumentException or CryptographicException or JsonException)
        { return new("unverified", "history_native_slots_unavailable"); }
    }
    public void Dispose() { if (_disposed) { return; } _disposed = true; _slots?.Dispose(); }

    private static bool Matches(SystemAutorun.Core.Models.StartupSourceScopeObservation scope, StartupHistoryBinding binding) => scope.State == "observed"
        && scope.ObserverSid == binding.ActorSid && scope.Source == binding.Source.Source && scope.SourceKind == binding.Source.Kind
        && scope.KnownFolder == binding.Source.KnownFolder && scope.SourceUserSid == binding.Source.UserSid
        && scope.DirectoryPath == binding.Source.Directory.Path && scope.DirectoryIdentity == binding.Source.Directory.Identity
        && scope.CaseSensitivity == binding.Source.Directory.CaseSensitivity;
}
