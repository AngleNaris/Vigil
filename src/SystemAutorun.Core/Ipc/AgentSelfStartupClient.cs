using System.Text.Json;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

// Shared self preparation/submission boundary. Context/fingerprint is a fresh
// precondition, NOT confirmation, ownership, a durable plan or native authority.
public sealed class AgentSelfStartupClient(AgentClient client, string rootDirectory)
{
    public async Task<SelfStartupMutationRequest> PrepareAsync(bool enabled, TimeSpan? timeout = null, CancellationToken token = default)
    {
        var response = await client.SendResponseAsync("startup-self-prepare", enabled ? "enable" : "disable",
            timeout: timeout, cancellationToken: token).ConfigureAwait(false);
        return ReadPreparation(response, enabled, rootDirectory);
    }

    // The caller obtains explicit confirmation and durably reserves this ID BEFORE
    // calling. This client never confirms, generates an ID, persists or retries.
    public async Task<AgentResponse> SubmitAsync(SelfStartupMutationRequest request, string operationId,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        if (SelfStartupMutationPolicy.ValidateRequest(request) is { } error)
        { throw new AgentCommandException(error, "Self submission requires valid, explicitly confirmed preparation."); }
        var id = OperationJournal.NormalizeId(operationId);
        var rootId = AgentProtocol.ForRoot(rootDirectory).RootId;
        if (client.RootId != rootId)
        { throw new ArgumentException("Self client transport and expected configuration root must match."); }
        // AgentClient alone knows whether a write was attempted. Pre-dispatch
        // exceptions remain unchanged; post-write Unknown retains the original ID.
        var response = await client.SendResponseAsync("startup-self-set", payload: request, timeout: timeout,
            cancellationToken: token, operationId: id).ConfigureAwait(false);
        return ReadAcknowledgement(response, id, rootId);
    }

    // expectedRootId is captured before sending. Reading an ACK never re-resolves
    // a possibly moved/unavailable filesystem root after the mutation exchange.
    public static AgentResponse ReadAcknowledgement(AgentResponse response, string operationId, string expectedRootId)
    {
        var id = OperationJournal.NormalizeId(operationId);
        try
        {
            if (expectedRootId is not { Length: 64 } || expectedRootId.Any(value => value is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))
                || response.ProtocolVersion != AgentProtocol.Version || response.RootId != expectedRootId
                || response.OperationId != id || string.IsNullOrWhiteSpace(response.Id) || response.Id.Length > AgentProtocol.MaximumRequestIdCharacters
                || response.Revision is not null || response.Ok && (response.Error is not null || response.ErrorCode is not null)
                || !response.Ok && (response.ErrorCode is null || response.ErrorCode.Length is < 1 or > 64
                    || response.ErrorCode.Any(value => value is not (>= 'a' and <= 'z') and not '_')))
            { throw new JsonException("Invalid self operation acknowledgement."); }
            if (!response.Ok) { return response with { Data = null }; }
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            if (data.ValueKind != JsonValueKind.Object || data.GetRawText().Length > AgentProtocol.MaximumResponseCharacters)
            { throw new JsonException("Missing or invalid self operation acknowledgement data."); }
            // Opaque historical driver evidence, never current business state,
            // permission to replay or proof of a Windows logon launch.
            return response with { Data = data.Clone() };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            var requestId = !string.IsNullOrWhiteSpace(response.Id) && response.Id.Length <= AgentProtocol.MaximumRequestIdCharacters ? response.Id : null;
            throw new AgentOutcomeUnknownException(id, error, requestId);
        }
    }

    public static SelfStartupMutationRequest ReadPreparation(AgentResponse response, bool enabled, string rootDirectory)
    {
        try
        {
            if (response.ProtocolVersion != AgentProtocol.Version || response.RootId != AgentProtocol.ForRoot(rootDirectory).RootId
                || string.IsNullOrWhiteSpace(response.Id) || response.Id.Length > AgentProtocol.MaximumRequestIdCharacters
                || response.OperationId is not null || response.Revision is not null || response.Replayed)
            { throw new JsonException("Invalid preparation envelope."); }
            // Failure Data is never evidence, including apparent confirmations.
            if (!response.Ok)
            {
                var code = response.ErrorCode;
                if (code is null || code.Length is < 1 or > 64 || code.Any(value => value is not (>= 'a' and <= 'z') and not '_'))
                { throw new JsonException("Invalid preparation error."); }
                throw new AgentCommandException(code, "Self preparation was refused; no mutation was requested.", requestId: response.Id);
            }
            if (response.Error is not null || response.ErrorCode is not null) { throw new JsonException("Contradictory preparation result."); }
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            var request = SelfStartupMutationContract.ReadRequest(data.GetRawText());
            if (request.Confirmed || request.Enabled != enabled || SelfStartupMutationPolicy.ValidateRequest(request with { Confirmed = true }) is not null)
            { throw new JsonException("Invalid preparation preconditions."); }
            return request;
        }
        catch (Exception error) when (error is not AgentCommandException && error is JsonException or InvalidOperationException or NotSupportedException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned invalid self preparation; no mutation was requested."); }
    }
}
