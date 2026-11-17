using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Agent;

internal sealed partial class AgentStartupService
{
    // Caller MUST own _reading. No nested admission, timeout or cancellation-only wrapper:
    // completion means the actual native task and every following workflow stage ended.
    private async Task<StartupInventorySnapshot> ReadInventoryCoreAsync(ConfigurationWorkspaceSnapshot configuration,
        StartupListQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revision = configuration.Revision;
        var native = await reader.ReadAsync(configuration.Configuration, query, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var current = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != revision) { return Unavailable(current.Revision, "configuration_changed"); }
        if (native is null || native.Entries is null || native.TotalActive < 0 || native.ManagedActive < 0 || native.Disabled < 0
            || (long)native.TotalActive + native.Disabled != native.Entries.Count || native.ManagedActive > native.TotalActive
            || native.ReadableSources is < 0 or > 6 || native.SourceIssues is null || native.SourceIssues.Count > 6
            || native.SourceIssues.Any(issue => !StartupListQuery.ValidIssue(issue)
                || (issue.Source == "backups" ? query.State == "active" : !query.IncludesSource(issue.Source)))
            || native.SourceIssues.Select(issue => issue.Source).Distinct(StringComparer.Ordinal).Count() != native.SourceIssues.Count)
        { return Unavailable(revision, "startup_source_invalid"); }

        var entries = new List<StartupEntry>(); var bytes = 0; var scanned = 0; var matched = 0;
        var truncated = native.Truncated; var errors = false; var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in native.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned == 4096) { truncated = true; break; }
            scanned++;
            if (entry is null || !StartupListQuery.ValidEntry(entry) || !ids.Add(entry.Id))
            { truncated = true; errors = true; continue; }
            if (!query.Matches(entry)) { continue; }
            matched++;
            errors |= !string.IsNullOrEmpty(entry.Error);
            if (entries.Count == query.Limit) { truncated = true; continue; }
            var size = JsonSerializer.SerializeToUtf8Bytes(entry, AgentProtocol.JsonOptions).Length;
            if (bytes + size > StartupListQuery.MaximumSerializedEntriesBytes) { truncated = true; continue; }
            entries.Add(entry); bytes += size;
        }
        if (native.ReadableSources == 0 && entries.Count == 0)
        { return Unavailable(revision, native.SourceIssues.Count > 0 && native.SourceIssues.All(issue => issue.Reason == "permission_denied")
            ? "permission_denied" : "startup_source_unavailable", native.SourceIssues); }
        var observedAt = DateTimeOffset.UtcNow;
        return new(1, revision, observedAt, observedAt, true,
            native.SourceIssues.Count > 0 ? "startup_source_unavailable" : errors ? "startup_entry_unavailable" : null,
            entries.OrderBy(entry => entry.Source, StringComparer.Ordinal).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray(),
            scanned, matched, truncated, native.IsElevated, errors, native.ReadableSources, native.SourceIssues);
    }
}
