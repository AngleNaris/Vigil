using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

public sealed partial class StartupManager
{
    private static readonly JsonSerializerOptions HistoryOperationOptions = new(JsonSerializerOptions.Strict)
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 10 };

    // Internal only: no transport gate is opened. There is deliberately no overload
    // combining context with caller candidate/record/IDs. This request owns the draft.
    internal async Task<HistoryDisableTransaction> PrepareHistoryDisableAsync(OperationExecutionContext context,
        Func<string>? ownedDirectory = null, CancellationToken token = default, Action<string>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(context); token.ThrowIfCancellationRequested(); context.RequireActiveFor(_paths);
        void Checkpoint(string step)
        { context.RequireActiveFor(_paths); checkpoint?.Invoke(step); context.RequireActiveFor(_paths); }
        var ticket = await HistoryOperationDraft.CaptureAsync(this, context, token, Checkpoint).ConfigureAwait(false);
        var draft = ticket.Draft; context.RequireActiveFor(_paths); Checkpoint("operation_draft_captured");
        var owner = await PrepareHistoryDisableCoreAsync(draft.Candidate, draft.Record, ownedDirectory, token, Checkpoint,
            draft.ConfigurationRevision, ticket).ConfigureAwait(false);
        try
        {
            await owner.PublishOperationLinkAsync(token, Checkpoint).ConfigureAwait(false);
            context.RequireActiveFor(_paths); return owner;
        }
        catch { await owner.DisposeAsync().ConfigureAwait(false); throw; }
    }

    // Opaque snapshot provenance, not confirmation or Native authority. Even an
    // internal raw-owner constructor cannot pair ctx metadata with another draft.
    internal sealed class HistoryOperationDraft
    {
        private readonly StartupManager _manager;
        internal OperationExecutionContext Context { get; }
        internal StartupHistoryDisableDraft Draft { get; }
        private HistoryOperationDraft(StartupManager manager, OperationExecutionContext context, StartupHistoryDisableDraft draft)
        { _manager = manager; Context = context; Draft = draft; }

        internal static async Task<HistoryOperationDraft> CaptureAsync(StartupManager manager, OperationExecutionContext context,
            CancellationToken token, Action<string>? checkpoint)
        {
            ArgumentNullException.ThrowIfNull(context); token.ThrowIfCancellationRequested(); context.RequireActiveFor(manager._paths);
            var request = context.Request;
            if (!string.Equals(request.Command, "startup-disable", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(request.Name) || request.Payload is not { ValueKind: JsonValueKind.Object } payload
                || request.ExpectedRevision is null)
            { throw new ArgumentException("A live shortcut-disable request snapshot is required.", nameof(context)); }
            var selection = payload.Deserialize<StartupInspectionRequest>(HistoryOperationOptions)
                ?? throw new JsonException("The frozen shortcut selection is missing.");
            var draft = await manager.CaptureHistoryDisableDraftAsync(request.Name, selection, request.ExpectedRevision, token, checkpoint).ConfigureAwait(false);
            context.RequireActiveFor(manager._paths); return new(manager, context, draft);
        }

        internal void Require(StartupManager manager, StartupHistoryPayload candidate, StartupBackupRecord record)
        {
            Context.RequireActiveFor(manager._paths);
            if (!ReferenceEquals(_manager, manager) || Draft.Candidate != candidate || Draft.Record != record)
            { throw new InvalidOperationException("The exact context-derived draft and manager are required."); }
        }
    }

    internal sealed partial class HistoryDisableTransaction
    {
        private readonly OperationExecutionContext? _operationContext;
        private bool _operationLinkActive;
        private bool _operationLinked;
        internal StartupHistoryOperationReference? OperationReference => _operationLinked && _operationContext is { } context
            ? new(context.OperationId, context.Fingerprint, TransactionId) : null;

        internal async Task PublishOperationLinkAsync(CancellationToken token, Action<string>? checkpoint)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_operationLinked) { throw new InvalidOperationException("The new operation link was already issued."); }
                _operationLinkActive = true;
                Session.PublishNewOperationLink(this, token, checkpoint);
                _operationLinked = true; VerifyPending(token);
            }
            finally { _operationLinkActive = false; _gate.Release(); }
        }

        internal StartupHistoryOperationReference RequireOperationLinkOwner(StartupHistoryCandidateSession session, CancellationToken token)
        {
            if (!_operationLinkActive || _operationLinked || _operationContext is null || !ReferenceEquals(Session, session)
                || _used || _native is not null || _ledgerVerified || _commitActive || _commitResult is not null)
            { throw new InvalidOperationException("Only the new matching pre-native operation owner may issue a link."); }
            VerifyPending(token);
            return new(_operationContext.OperationId, _operationContext.Fingerprint, TransactionId);
        }
    }
}
