using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Models;

// A reviewed native observation plus the editable task form, not native write authority.
public sealed record StartupAdoptionRequest(StartupEntry ExpectedEntry, AddTaskRequest Task, bool Confirmed = false)
{
    public StartupListQuery Validate(string entryId)
    {
        if (!Confirmed) { throw new AgentCommandException("authorization_required", "Adoption requires explicit confirmation: the native launcher is not disabled and existing automatic policies may also run the task."); }
        if (ExpectedEntry is null || Task is null || !StartupListQuery.ValidEntry(ExpectedEntry)
            || !string.Equals(entryId, ExpectedEntry.Id, StringComparison.Ordinal))
        { throw new ArgumentException("Adoption requires the exact selected entry and a reviewed task form."); }
        if (ExpectedEntry.IsDisabled || ExpectedEntry.IsManaged || !string.IsNullOrEmpty(ExpectedEntry.Error)
            || string.IsNullOrWhiteSpace(ExpectedEntry.Target))
        { throw new ArgumentException("Only an active, readable, unowned startup entry with target metadata can be adopted."); }
        // Metadata only: never resolve or probe the native target/working directory here.
        var scope = ExpectedEntry.Source is "Startup" or "HKCU" ? "user" : "machine";
        var kind = ExpectedEntry.Source is "Startup" or "Common Startup" ? "shortcut" : "registry";
        var search = ExpectedEntry.Name.Length <= 256 && !ExpectedEntry.Name.Any(char.IsControl) ? ExpectedEntry.Name : null;
        return new StartupListQuery(scope, kind, "active", search, 200).Validate();
    }
}

// These describe this operation's calls, NOT durable native state or workload liveness.
public sealed record StartupAdoptionResult(string EntryId, ConfigurationMutationResult Configuration,
    bool NativeWritePerformed, bool ExecutionRequested);
