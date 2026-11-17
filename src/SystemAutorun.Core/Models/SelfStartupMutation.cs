using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Models;

// Full bounded native-value fingerprint, not the decoded/trimmed read-only status.
// This is a precondition, NEVER an ownership token or an atomic Windows CAS.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelfStartupNativeState(bool? Registered, uint? ValueType = null, int? ByteCount = null, string? Sha256 = null)
{
    public static SelfStartupNativeState FromNativeBytes(uint valueType, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SelfStartupMutationPolicy.MaximumNativeDataBytes) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
        return new(true, valueType, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelfStartupMutationRequest
{
    public required int SchemaVersion { get; init; }
    public required bool Enabled { get; init; }
    public required bool Confirmed { get; init; }
    public required string TargetContextId { get; init; }
    public required SelfStartupNativeState ExpectedRegistration { get; init; }
}

public static class SelfStartupMutationContract
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions InputJson = new(AgentProtocol.JsonOptions)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    // Future CLI/RPC entry points must use this reader, not the permissive shared options.
    public static SelfStartupMutationRequest ReadRequest(string json)
    {
        if (json is null || json.Length > AgentProtocol.MaximumRequestCharacters) { throw new JsonException("Invalid self startup request bounds."); }
        return JsonSerializer.Deserialize<SelfStartupMutationRequest>(json, InputJson)
            ?? throw new JsonException("Missing self startup request.");
    }
}

public enum SelfStartupTransition { Create, Delete, VerifyEnabled, VerifyAbsent }

// A plan is not permission to write. The executor must independently verify package,
// actor and physical root, hold the cross-root lease, journal, reread and verify again.
public sealed class SelfStartupMutationPlan
{
    public string TargetContextId { get; }
    public SelfStartupTransition Transition { get; }
    public SelfStartupNativeState ExpectedBefore { get; }
    public SelfStartupNativeState ExpectedAfter { get; }
    public bool RequiresNativeWrite => Transition is SelfStartupTransition.Create or SelfStartupTransition.Delete;
    internal SelfStartupMutationPlan(string contextId, SelfStartupTransition transition, SelfStartupNativeState before, SelfStartupNativeState after)
    { TargetContextId = contextId; Transition = transition; ExpectedBefore = before; ExpectedAfter = after; }
}

public sealed class SelfStartupMutationDecision
{
    public SelfStartupMutationPlan? Plan { get; }
    public string? ErrorCode { get; }
    private SelfStartupMutationDecision(SelfStartupMutationPlan? plan, string? errorCode) { Plan = plan; ErrorCode = errorCode; }
    internal static SelfStartupMutationDecision Refuse(string code) => new(null, code);
    internal static SelfStartupMutationDecision Planned(SelfStartupMutationPlan plan) => new(plan, null);
}

public static class SelfStartupMutationPolicy
{
    public const int SchemaVersion = SelfStartupMutationContract.SchemaVersion;
    public const int MaximumNativeDataBytes = (StartupRegistrationContract.MaximumTextCharacters + 1) * 2;

    public static string? ValidateRequest(SelfStartupMutationRequest? request)
    {
        if (request is null || request.SchemaVersion != SchemaVersion || !ValidDigest(request.TargetContextId)
            || !ValidState(request.ExpectedRegistration)) { return "invalid_argument"; }
        return request.Confirmed ? null : "authorization_required";
    }

    // serverContextId binds verified actor/root/package facts; it is NOT a secret capability.
    // Both server arguments must come from the trusted executor, NEVER the request/status.
    // This pure function performs no filesystem, registry, lease, journal or process work.
    public static SelfStartupMutationDecision Evaluate(SelfStartupMutationRequest? request, string? serverContextId,
        string? serverTargetCommand, SelfStartupNativeState? current)
    {
        if (ValidateRequest(request) is { } error) { return SelfStartupMutationDecision.Refuse(error); }
        if (!ValidDigest(serverContextId) || !ValidTargetShape(serverTargetCommand))
        { return SelfStartupMutationDecision.Refuse("self_target_unavailable"); }
        if (!string.Equals(request!.TargetContextId, serverContextId, StringComparison.Ordinal))
        { return SelfStartupMutationDecision.Refuse("self_context_changed"); }
        if (!ValidState(current)) { return SelfStartupMutationDecision.Refuse("self_registration_invalid"); }
        if (request.ExpectedRegistration != current) { return SelfStartupMutationDecision.Refuse("self_registration_changed"); }

        var target = SelfStartupNativeState.FromNativeBytes(1, Encoding.Unicode.GetBytes(serverTargetCommand + '\0'));
        var absent = new SelfStartupNativeState(false);
        SelfStartupTransition transition;
        if (current!.Registered == false) { transition = request.Enabled ? SelfStartupTransition.Create : SelfStartupTransition.VerifyAbsent; }
        else if (current == target) { transition = request.Enabled ? SelfStartupTransition.VerifyEnabled : SelfStartupTransition.Delete; }
        else { return SelfStartupMutationDecision.Refuse("self_registration_conflict"); }
        return SelfStartupMutationDecision.Planned(new(serverContextId!, transition, current, request.Enabled ? target : absent));
    }

    // False includes a changed/invalid context or unreadable/malformed native result;
    // the executor must report uncertainty, not retry, rollback or assume no write occurred.
    public static bool MatchesPostcondition(SelfStartupMutationPlan? plan, string? serverContextId, SelfStartupNativeState? current) =>
        plan is not null && ValidDigest(serverContextId) && string.Equals(plan.TargetContextId, serverContextId, StringComparison.Ordinal)
        && ValidState(current) && current == plan.ExpectedAfter;

    public static bool ValidState(SelfStartupNativeState? value)
    {
        if (value?.Registered is null) { return false; }
        if (value.Registered == false) { return value == new SelfStartupNativeState(false); }
        return value.ValueType is not null && value.ByteCount is >= 0 and <= MaximumNativeDataBytes && ValidDigest(value.Sha256);
    }

    private static bool ValidDigest(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidTargetShape(string? command)
    {
        if (string.IsNullOrEmpty(command) || command.Length > StartupRegistrationContract.MaximumTextCharacters) { return false; }
        for (var index = 0; index < command.Length; index++)
        {
            if (!char.IsSurrogate(command[index])) { continue; }
            if (!char.IsHighSurrogate(command[index]) || index + 1 >= command.Length || !char.IsLowSurrogate(command[++index])) { return false; }
        }
        try { return StartupRegistrationContract.ParseSelfRoot(command) is not null; }
        catch (PathTooLongException) { return false; }
    }
}
