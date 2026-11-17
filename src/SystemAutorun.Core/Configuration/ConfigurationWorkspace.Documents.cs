using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Configuration;

public sealed partial class ConfigurationWorkspace
{
    public async Task<ConfigurationDocumentSnapshot> ReadDocumentAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        using var files = new ConfigurationDocumentFiles(_paths);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var revision = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await files.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (revision != await ReadRevisionAsync(cancellationToken).ConfigureAwait(false)) { continue; }
            var utf8Valid = true;
            try { _ = ConfigurationDocumentRequest.Text(bytes); }
            catch (DecoderFallbackException) { utf8Valid = false; }
            return new(1, revision, DateTimeOffset.UtcNow, ConfigurationDocumentRequest.Encode(bytes),
                ConfigurationDocumentRequest.Hash(bytes), bytes.Length, utf8Valid);
        }
        throw new IOException("Configuration is being changed outside Agent. Retry after the external edit finishes.");
    }

    public async Task<ConfigurationValidationResult> ValidateDocumentAsync(ConfigurationDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = request.Decode();
        await using var lease = await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        using var files = new ConfigurationDocumentFiles(_paths);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var revision = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
            var original = await files.ReadAsync(cancellationToken).ConfigureAwait(false);
            AutorunConfiguration? candidate = null; string? error = null;
            try { candidate = await _loader.LoadTextAsync(ConfigurationDocumentRequest.Text(bytes), _paths.RootDirectory, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is ConfigurationException or ArgumentException)
            { error = exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message; }
            var changes = candidate is null ? null : await CompareDocumentAsync(original, candidate, cancellationToken).ConfigureAwait(false);
            if (revision != await ReadRevisionAsync(cancellationToken).ConfigureAwait(false)) { continue; }
            return new(1, candidate is not null, revision, DateTimeOffset.UtcNow, ConfigurationDocumentRequest.Hash(bytes),
                bytes.Length, candidate?.Tasks.Count ?? 0, changes, error);
        }
        throw new IOException("Configuration changed during validation. Reload before applying.");
    }

    public async Task<WorkspaceMutation<ConfigurationApplyResult>> ApplyDocumentAsync(ConfigurationDocumentRequest request,
        string expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        if (!request.Confirmed) { throw new ConfigurationConfirmationRequiredException(); }
        var bytes = request.Decode();
        await using var lease = await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        using var files = new ConfigurationDocumentFiles(_paths);
        await CheckRevisionAsync(cancellationToken).ConfigureAwait(false);
        var original = await files.ReadAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await _loader.LoadTextAsync(ConfigurationDocumentRequest.Text(bytes), _paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        var changes = await CompareDocumentAsync(original, candidate, cancellationToken).ConfigureAwait(false);
        var hash = ConfigurationDocumentRequest.Hash(bytes);
        if (original.AsSpan().SequenceEqual(bytes))
        {
            await CheckRevisionAsync(cancellationToken).ConfigureAwait(false);
            return new(new(true, false, hash, bytes.Length, candidate.Tasks.Count, null, changes), expectedRevision, DateTimeOffset.UtcNow);
        }

        var directory = files.PinBackupDirectory();
        var nonce = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(_paths.RootDirectory, $".autorun.yaml.{nonce}.tmp");
        var backup = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{nonce}-apply-autorun.yaml");
        var created = false; var retainTemporary = false;
        try
        {
            // Mark ownership after CreateNew succeeds, even if the subsequent durable write fails.
            var security = new FileInfo(_paths.ConfigPath).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            await using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew,
                System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough, security))
            {
                created = true;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            await CheckRevisionAsync(cancellationToken).ConfigureAwait(false);
            if (!(await files.ReadAsync(cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(original))
            { throw new IOException("autorun.yaml changed while the replacement was being prepared."); }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The fixed, pinned backup chain cannot leave this volume. Preserve destination ACLs.
                File.Replace(temporary, _paths.ConfigPath, backup, ignoreMetadataErrors: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var unchanged = false;
                try { unchanged = (await files.ReadAsync(CancellationToken.None).ConfigureAwait(false)).AsSpan().SequenceEqual(original); }
                catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or ConfigurationException or ArgumentException) { }
                if (!unchanged)
                {
                    retainTemporary = true;
                    throw new ConfigurationOutcomeUnknownException($"Replacement outcome is unknown. Inspect the operation and recovery files: {backup}; {temporary}. Do not replay with a new operation ID.", exception);
                }
                if ((exception.HResult & 0xFFFF) is 32 or 33) { throw new ConfigurationSourceBusyException(exception); }
                throw;
            }
            // Commit is no longer cancellable. Never describe a committed replacement as 'cancelled'.
            string revision;
            try { revision = await ReadRevisionAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new ConfigurationOutcomeUnknownException($"YAML was replaced, but its resulting revision could not be confirmed. Backup: {backup}. Inspect the same operation ID.", exception); }
            return new(new(true, true, hash, bytes.Length, candidate.Tasks.Count, backup, changes), revision, DateTimeOffset.UtcNow);
        }
        finally
        {
            // Only this call's exact CreateNew sibling is eligible; unknown outcomes retain recovery material.
            if (created && !retainTemporary && File.Exists(temporary)) { File.Delete(temporary); }
        }

        async Task CheckRevisionAsync(CancellationToken token)
        {
            var actual = await ReadRevisionAsync(token).ConfigureAwait(false);
            if (actual != expectedRevision) { throw new ConfigurationConflictException(expectedRevision, actual); }
        }
    }

    private async Task<ConfigurationDocumentChanges> CompareDocumentAsync(byte[] original, AutorunConfiguration candidate, CancellationToken token)
    {
        AutorunConfiguration current;
        try { current = await _loader.LoadTextAsync(ConfigurationDocumentRequest.Text(original), _paths.RootDirectory, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ConfigurationException or ArgumentException)
        { return new(false, [], [], [], false, "current_yaml_invalid; explicit full-document replacement can repair it"); }
        var before = current.Tasks.ToDictionary(task => task.Name, StringComparer.OrdinalIgnoreCase);
        var after = candidate.Tasks.ToDictionary(task => task.Name, StringComparer.OrdinalIgnoreCase);
        return new(true, after.Keys.Where(name => !before.ContainsKey(name)).ToArray(),
            before.Keys.Where(name => !after.ContainsKey(name)).ToArray(),
            after.Keys.Where(name => before.TryGetValue(name, out var prior)
                && JsonSerializer.Serialize(prior, AgentProtocol.JsonOptions) != JsonSerializer.Serialize(after[name], AgentProtocol.JsonOptions)).ToArray(),
            current.Settings != candidate.Settings);
    }
}

public sealed class ConfigurationConfirmationRequiredException() : InvalidOperationException("Full YAML replacement requires explicit confirmation (--yes).");
