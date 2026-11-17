using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class SelfStartupMutationTests
{
    private const string Command = "\"C:\\Package\\Vigil.exe\" --background --root \"C:\\owned-root\"";
    private static readonly string Context = new('a', 64);
    private static readonly SelfStartupNativeState Absent = new(false);
    private static SelfStartupNativeState Native(string command = Command, uint type = 1, string ending = "\0") =>
        SelfStartupNativeState.FromNativeBytes(type, Encoding.Unicode.GetBytes(command + ending));
    private static SelfStartupMutationRequest Request(bool enabled = true, SelfStartupNativeState? expected = null) => new()
    { SchemaVersion = 1, Enabled = enabled, Confirmed = true, TargetContextId = Context, ExpectedRegistration = expected ?? Absent };
    private static void Refused(string code, SelfStartupMutationDecision result) { Assert.Null(result.Plan); Assert.Equal(code, result.ErrorCode); }

    [Theory]
    [InlineData(true, false, SelfStartupTransition.Create)]
    [InlineData(false, true, SelfStartupTransition.Delete)]
    [InlineData(true, true, SelfStartupTransition.VerifyEnabled)]
    [InlineData(false, false, SelfStartupTransition.VerifyAbsent)]
    public void FourTransitionsArePlansNotCompletedWrites(bool enabled, bool exists, SelfStartupTransition transition)
    {
        var before = exists ? Native() : Absent;
        var result = SelfStartupMutationPolicy.Evaluate(Request(enabled, before), Context, Command, before);
        Assert.Null(result.ErrorCode); var plan = Assert.IsType<SelfStartupMutationPlan>(result.Plan);
        Assert.Equal(transition, plan.Transition); Assert.Equal(Context, plan.TargetContextId); Assert.Equal(before, plan.ExpectedBefore);
        Assert.Equal(enabled ? Native() : Absent, plan.ExpectedAfter);
        Assert.Equal(transition is SelfStartupTransition.Create or SelfStartupTransition.Delete, plan.RequiresNativeWrite);
        Assert.True(SelfStartupMutationPolicy.MatchesPostcondition(plan, Context, plan.ExpectedAfter));
        Assert.False(SelfStartupMutationPolicy.MatchesPostcondition(plan, new string('b', 64), plan.ExpectedAfter));
        Assert.False(SelfStartupMutationPolicy.MatchesPostcondition(plan, Context, enabled ? Absent : Native()));
    }

    public static IEnumerable<object[]> ForeignValues()
    {
        var values = new[]
        {
            Native(type: 2), Native(type: 3), Native(type: 0), Native(type: uint.MaxValue),
            Native(Command.Replace("Package", "PACKAGE", StringComparison.Ordinal)),
            Native(Command.Replace("owned-root", "OWNED-root", StringComparison.Ordinal)),
            Native(Command.Replace("owned-root", "other-root", StringComparison.Ordinal)),
            Native(Command.Replace("Package", "other-package", StringComparison.Ordinal)),
            Native(Command + " --extra"), Native(ending: ""), Native(ending: "\0\0"),
            Native(Command.Replace("owned-root", "owned\0-root", StringComparison.Ordinal)),
            Native("%UNEXPANDED% --background --root %ROOT%"),
            SelfStartupNativeState.FromNativeBytes(1, new byte[] { 0x41 }),
            Native() with { ByteCount = 0 },
        };
        foreach (var enabled in new[] { false, true })
        foreach (var value in values) { yield return [enabled, value]; }
    }

    [Theory]
    [MemberData(nameof(ForeignValues))]
    public void MatchingAnExpectedForeignFingerprintNeverAuthorizesTakeover(bool enabled, SelfStartupNativeState value)
    {
        Refused("self_registration_conflict", SelfStartupMutationPolicy.Evaluate(Request(enabled, value), Context, Command, value));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConfirmationIsRequiredEvenForIdempotentPlans(bool enabled)
    {
        foreach (var value in new[] { Absent, Native() })
        { Refused("authorization_required", SelfStartupMutationPolicy.Evaluate(Request(enabled, value) with { Confirmed = false }, Context, Command, value)); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompleteNativePreconditionMustStillMatch(bool enabled)
    {
        Refused("self_registration_changed", SelfStartupMutationPolicy.Evaluate(Request(enabled), Context, Command, Native()));
        Refused("self_registration_changed", SelfStartupMutationPolicy.Evaluate(Request(enabled, Native()), Context, Command, Absent));
        Refused("self_registration_changed", SelfStartupMutationPolicy.Evaluate(Request(enabled, Native()), Context, Command, Native(ending: "\0\0")));
        Refused("self_context_changed", SelfStartupMutationPolicy.Evaluate(Request(enabled), new string('b', 64), Command, Absent));
    }

    public static IEnumerable<object?[]> InvalidStates()
    {
        yield return [null]; yield return [new SelfStartupNativeState(null)];
        yield return [new SelfStartupNativeState(false, 1)]; yield return [new SelfStartupNativeState(false, Sha256: new string('a', 64))];
        yield return [new SelfStartupNativeState(true)]; yield return [Native() with { ValueType = null }];
        yield return [Native() with { ByteCount = null }]; yield return [Native() with { ByteCount = -1 }];
        yield return [Native() with { ByteCount = SelfStartupMutationPolicy.MaximumNativeDataBytes + 1 }];
        yield return [Native() with { Sha256 = null }]; yield return [Native() with { Sha256 = "" }];
        yield return [Native() with { Sha256 = new string('A', 64) }]; yield return [Native() with { Sha256 = new string('g', 64) }];
    }

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void MalformedExpectedAndObservedStatesFailClosedWithoutThrowing(SelfStartupNativeState? invalid)
    {
        Assert.False(SelfStartupMutationPolicy.ValidState(invalid));
        Refused("invalid_argument", SelfStartupMutationPolicy.Evaluate(Request() with { ExpectedRegistration = invalid! }, Context, Command, Absent));
        Refused("self_registration_invalid", SelfStartupMutationPolicy.Evaluate(Request(), Context, Command, invalid));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MalformedContextCannotBecomeAuthority(string? invalid)
    {
        Refused("invalid_argument", SelfStartupMutationPolicy.Evaluate(Request() with { TargetContextId = invalid! }, Context, Command, Absent));
        Refused("self_target_unavailable", SelfStartupMutationPolicy.Evaluate(Request(), invalid, Command, Absent));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("arbitrary.exe")]
    [InlineData("\"\\\\offline.invalid\\Vigil.exe\" --background --root \"C:\\owned-root\"")]
    [InlineData("\"C:\\Package\\Other.exe\" --background --root \"C:\\owned-root\"")]
    [InlineData("\"C:\\Package\\Vigil.exe\" --background --root \"C:\\own\u202eed-root\"")]
    public void MalformedServerTargetHasNoPlan(string? invalid)
    {
        Refused("self_target_unavailable", SelfStartupMutationPolicy.Evaluate(Request(), Context, invalid, Absent));
    }

    [Fact]
    public void OversizedAndBrokenUtf16TargetsFailBeforeFingerprinting()
    {
        foreach (var bad in new[] { new string('x', 8193), Command.Replace("owned-root", "owned\ud800-root"), Command.Replace("owned-root", "owned\udc00-root") })
        { Refused("self_target_unavailable", SelfStartupMutationPolicy.Evaluate(Request(), Context, bad, Absent)); }
        Refused("invalid_argument", SelfStartupMutationPolicy.Evaluate(null, Context, Command, Absent));
        Refused("invalid_argument", SelfStartupMutationPolicy.Evaluate(Request() with { SchemaVersion = 2 }, Context, Command, Absent));
    }

    [Fact]
    public void NativeFingerprintIncludesTypeExactBytesLengthAndOneUtf16Terminator()
    {
        var empty = SelfStartupNativeState.FromNativeBytes(1, []);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", empty.Sha256);
        Assert.Equal(0, empty.ByteCount); Assert.True(SelfStartupMutationPolicy.ValidState(empty));
        var target = Native(); var bytes = Encoding.Unicode.GetBytes(Command + '\0');
        Assert.Equal(bytes.Length, target.ByteCount); Assert.Equal((Command.Length + 1) * 2, target.ByteCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), target.Sha256);
        Assert.NotEqual(target, Native(ending: "")); Assert.NotEqual(target, Native(ending: "\0\0")); Assert.NotEqual(target, Native(type: 2));
        var maximum = new byte[SelfStartupMutationPolicy.MaximumNativeDataBytes];
        Assert.True(SelfStartupMutationPolicy.ValidState(SelfStartupNativeState.FromNativeBytes(3, maximum)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SelfStartupNativeState.FromNativeBytes(3, new byte[maximum.Length + 1]));
    }

    [Fact]
    public void AnyMalformedOrUnavailablePostconditionStaysUnconfirmed()
    {
        var plan = SelfStartupMutationPolicy.Evaluate(Request(), Context, Command, Absent).Plan!;
        foreach (var row in InvalidStates()) { Assert.False(SelfStartupMutationPolicy.MatchesPostcondition(plan, Context, (SelfStartupNativeState?)row[0])); }
        Assert.False(SelfStartupMutationPolicy.MatchesPostcondition(null, Context, Native()));
        Assert.False(SelfStartupMutationPolicy.MatchesPostcondition(plan, null, Native()));
        Assert.False(SelfStartupMutationPolicy.MatchesPostcondition(plan, Context, Native(ending: "\0\0")));
    }

    [Theory]
    [InlineData("schemaVersion")] [InlineData("enabled")] [InlineData("confirmed")]
    [InlineData("targetContextId")] [InlineData("expectedRegistration")]
    public void EveryRequestMemberMustBeExplicitlyPresentIncludingBothBooleans(string member)
    {
        var json = JsonSerializer.SerializeToNode(Request(), AgentProtocol.JsonOptions)!.AsObject(); json.Remove(member);
        Assert.Throws<JsonException>(() => json.Deserialize<SelfStartupMutationRequest>(AgentProtocol.JsonOptions));
    }

    [Theory]
    [InlineData("executable")] [InlineData("hive")] [InlineData("key")] [InlineData("rootAssociation")]
    [InlineData("configurationRevision")] [InlineData("verified")] [InlineData("operationId")]
    public void RequestCannotSelectTargetsClaimAuthorityOrSmuggleEnvelopeFields(string field)
    {
        var json = JsonSerializer.SerializeToNode(Request(), AgentProtocol.JsonOptions)!.AsObject(); json[field] = "untrusted";
        Assert.Throws<JsonException>(() => json.Deserialize<SelfStartupMutationRequest>(AgentProtocol.JsonOptions));
        var clean = JsonSerializer.SerializeToNode(Request(), AgentProtocol.JsonOptions)!.AsObject(); clean["expectedRegistration"]![field] = "untrusted";
        Assert.Throws<JsonException>(() => clean.Deserialize<SelfStartupMutationRequest>(AgentProtocol.JsonOptions));
    }

    [Theory]
    [InlineData("schemaVersion")] [InlineData("enabled")] [InlineData("confirmed")]
    [InlineData("targetContextId")] [InlineData("expectedRegistration")]
    public void StrictEntryReaderRejectsDuplicateAndCaseAliasMembers(string member)
    {
        var node = JsonSerializer.SerializeToNode(Request(), AgentProtocol.JsonOptions)!.AsObject();
        var json = node.ToJsonString(); var value = member is "enabled" or "confirmed" ? "false" : node[member]!.ToJsonString();
        var duplicate = json[..^1] + ",\"" + member + "\":" + value + "}";
        Assert.Throws<JsonException>(() => SelfStartupMutationContract.ReadRequest(duplicate));
        var alias = char.ToUpperInvariant(member[0]) + member[1..];
        var aliased = json[..^1] + ",\"" + alias + "\":" + value + "}";
        Assert.Throws<JsonException>(() => SelfStartupMutationContract.ReadRequest(aliased));
    }

    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("false")]
    public void StrictEntryReaderRequiresAnObject(string json)
    { Assert.Throws<JsonException>(() => SelfStartupMutationContract.ReadRequest(json)); }

    [Fact]
    public void StrictEntryReaderBoundsInputAndRejectsNestedDuplicateNativeState()
    {
        Assert.Throws<JsonException>(() => SelfStartupMutationContract.ReadRequest(null!));
        Assert.Throws<JsonException>(() => SelfStartupMutationContract.ReadRequest(new string(' ', AgentProtocol.MaximumRequestCharacters + 1)));
        var json = JsonSerializer.Serialize(Request(), AgentProtocol.JsonOptions).Replace("\"registered\":false", "\"registered\":false,\"registered\":true", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => SelfStartupMutationContract.ReadRequest(json));
    }

    [Fact]
    public void ExplicitFalseRoundTripsAndPlanningDoesNotMutateInputs()
    {
        var request = Request(false); var before = JsonSerializer.Serialize(request, AgentProtocol.JsonOptions);
        var decoded = SelfStartupMutationContract.ReadRequest(before);
        Assert.False(decoded.Enabled); Assert.True(decoded.Confirmed); Assert.Equal(request, decoded);
        Assert.Null(SelfStartupMutationPolicy.Evaluate(decoded, Context, Command, Absent).ErrorCode);
        Assert.Equal(before, JsonSerializer.Serialize(decoded, AgentProtocol.JsonOptions));
        Assert.Null(SelfStartupMutationPolicy.Evaluate(Request(), Context, Command.Replace("owned-root", "owned-\ud83c\udf1f-root"), Absent).ErrorCode);
    }
}
