using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

// G1-self-package-admission-r1: trust the CURRENT running published App/Agent
// package + explicit confirmation, not publisher certification. No certificate,
// trust DB, arbitrary target, elevation, target launch or automatic retry.
public sealed class SelfStartupRegistrationService
{
    public const string Command = "startup-self-set";
    private readonly SelfStartupJournal _journal;
    private readonly bool _fixture;

    public string JournalInstanceId => _journal.InstanceId;
    public SelfStartupRegistrationService(ProjectPaths paths) : this(new OperationJournal(paths)) { }
    // Bind to the host's ONE Journal; its own immutable paths are the sole root.
    // A separate paths/journal pair is deliberately not accepted.
    public SelfStartupRegistrationService(OperationJournal journal) => _journal = SelfStartupJournal.ForCurrentPackage(journal);

    // Internal isolated counterpart of this exact service; NEVER a public host,
    // CLI/RPC parameter, production package override or real Run test switch.
    internal SelfStartupRegistrationService(SelfStartupJournal fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!fixture.IsFixture) { throw new ArgumentException("A GUID-only service fixture is required.", nameof(fixture)); }
        _journal = fixture; _fixture = true;
    }

    // Fresh point-in-time eligibility, NOT authorization, CAS or a durable plan.
    // Existing schema only; caller must explicitly confirm then execute/revalidate.
    // No actor lock, Journal admission, control directory or receipt is created.
    public async Task<SelfStartupMutationRequest> PrepareAsync(bool enabled, CancellationToken token = default)
    {
        try { return await _journal.PrepareAsync(enabled, token).ConfigureAwait(false); }
        catch (SelfStartupPlanningException error) { throw Refused(error.Reason is "self_context_changed" or "self_registration_conflict" ? error.Reason : "self_registry_unavailable"); }
        catch (SelfStartupTargetException) { throw Refused("self_target_unavailable"); }
        catch (SelfStartupNativeReadException) { throw Refused("self_registry_unavailable"); }
        catch (UnauthorizedAccessException) { throw Refused("self_context_changed"); }
        catch (IOException) { throw Refused("self_target_unavailable"); }
    }

    // Server binds the physical root/current package at construction. The envelope
    // has no target path, native slot, SID or admission-storage input. The shared
    // Journal clones its strict payload and rejects unsafe replay/header aliases.
    public async Task<AgentResponse> ExecuteAsync(AgentRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command != Command) { throw Refused("invalid_argument"); }
        // Hashing is deliberately retained; never run the package pipeline on a
        // GUI caller's dispatcher even when Journal IO happens to complete inline.
        return await Task.Run(() => _journal.RunAsync(_fixture ? request with { Command = SelfStartupJournal.FixtureCommand } : request, token: token), token).ConfigureAwait(false);
    }

    private static AgentCommandException Refused(string code) => new(code, "Self registration preparation or request was refused.");
}
