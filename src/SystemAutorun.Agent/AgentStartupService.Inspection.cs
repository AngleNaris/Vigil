using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Agent;

internal sealed partial class AgentStartupService
{
    internal async Task<StartupInspectionSnapshot> InspectAsync(string entryId, StartupInspectionRequest request,
        string expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Validate(entryId);
        if (expectedRevision is not { Length: 64 } || !expectedRevision.All(char.IsAsciiHexDigit))
        { throw new ArgumentException("Inspection requires the configuration revision from startup list."); }
        var configuration = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.Revision != expectedRevision)
        { throw new ConfigurationConflictException(expectedRevision, configuration.Revision); }
        // Reuses the bounded worker/retained scan slot. No new path, target, archive or COM probe.
        var observation = await ReadAsync(query, cancellationToken).ConfigureAwait(false);
        return ComposeInspection(observation, entryId, request, expectedRevision);
    }

    private static StartupInspectionSnapshot ComposeInspection(StartupInventorySnapshot observation, string entryId,
        StartupInspectionRequest request, string expectedRevision)
    {
        if (observation.ConfigurationRevision != expectedRevision)
        { throw new ConfigurationConflictException(expectedRevision, observation.ConfigurationRevision); }
        if (!observation.Available)
        { throw new AgentCommandException(observation.Reason == "permission_denied" ? "startup_preflight_permission_denied" : "startup_preflight_unavailable",
            $"Startup inspection unavailable ({observation.Reason ?? "startup_source_unavailable"}); nothing was changed or executed."); }
        if (observation.Truncated || observation.SourceIssues.Any(issue => issue.Source == request.Source
            || request.IsDisabled && issue.Source == "backups"))
        { throw new AgentCommandException("startup_inventory_incomplete", "Selected startup source is incomplete; no operation was prepared for native submission."); }
        var matches = observation.Entries.Where(entry => entry.Id == entryId).ToArray();
        if (matches.Length != 1 || matches[0].Source != request.Source || matches[0].Name != request.Name
            || matches[0].IsDisabled != request.IsDisabled || matches[0].MetadataDigest != request.MetadataDigest)
        { throw new AgentCommandException("startup_entry_changed", "Selected startup metadata changed or disappeared. Re-read and review; inspection grants no native write authority."); }
        var entry = matches[0];
        return new(1, observation.ConfigurationRevision, observation.CompletedAt, observation.ObservedAt!.Value,
            entry, entry.MetadataDigest, entry.IsDisabled ? "not_checked" : "not_applicable", observation.IsElevated,
            NativeWriteReady: false, NativeWritePerformed: false, ExecutionRequested: false, StartupInspectionPolicy.Describe(entry),
            request.RebuildInput is null ? null : StartupRebuildInputPolicy.Preview(request.RebuildInput));
    }
}
