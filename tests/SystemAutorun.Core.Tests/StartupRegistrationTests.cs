using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class StartupRegistrationTests
{
    private const string Root = @"C:\owned-root";
    private static StartupRegistrationSnapshot Self(string? command = null) => StartupRegistrationContract.Observe("self",
        new(true, "String", command ?? "\"C:\\app\\Vigil.exe\" --background --root \"C:\\owned-root\""), Root);
    private static StartupRegistrationSnapshot Read(StartupRegistrationSnapshot snapshot, string kind = "self") =>
        AgentStartupRegistrationClient.ReadObservation(JsonSerializer.SerializeToElement(snapshot, AgentProtocol.JsonOptions), kind, Root);

    [Fact]
    public void CanonicalSelfIsLexicalAssociationNotAnEnabledOrTrustClaim()
    {
        var status = Read(Self());
        Assert.Equal("same-root-lexical", status.RootAssociation);
        Assert.Equal(Root, status.AssociatedRoot);
        Assert.Equal("agent-user-global", status.RegistrationScope);
        Assert.True(status.Registration!.Registered); Assert.Null(status.Registration.Enabled);
        Assert.False(AgentProtocol.IsMutation("startup-self-status"));
        Assert.False(AgentProtocol.IsMutation("startup-boot-status"));
        Assert.Equal("other-root-lexical", Read(Self("\"C:\\app\\Vigil.exe\" --background --root \"D:\\other\"")).RootAssociation);
    }

    [Theory]
    [InlineData("\"\\\\offline.invalid\\app\\Vigil.exe\" --background --root \"C:\\owned-root\"")]
    [InlineData("\"C:\\app\\Vigil.exe\" --background --root \"\\\\offline.invalid\\root\"")]
    [InlineData("\"C:\\app\\Vigil.exe\" --background --root \"C:\\owned-root\" --root \"D:\\other\"")]
    [InlineData("\"C:\\app\\Vigil.exe\" --background --root \"C:\\owned-root\" --background --root \"D:\\other\"")]
    [InlineData("\"C:\\app\\Vigil.exe\" --background --root \"C:\\owned-root\\\"")]
    [InlineData("\"C:\\app\\other.exe\" --background --root \"C:\\owned-root\"")]
    [InlineData("\"C:\\app\\Vigil.exe\" --background --root \"C:\\own\u202Eed-root\"")]
    [InlineData("\"C:\\app\\Vigil.exe\" --background --root \"relative\"")]
    [InlineData("%APP% --background --root %ROOT%")]
    public void UnrecognizedCommandsStayObservedButAssociationUnknown(string command)
    {
        var status = Read(Self(command));
        Assert.True(status.Available); Assert.True(status.Registration!.Registered);
        Assert.Equal(command, status.Registration.Command); Assert.Null(status.AssociatedRoot); Assert.Equal("unknown", status.RootAssociation);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("boot")]
    public void AbsentAndUnavailableAreDifferent(string kind)
    {
        var absent = Read(StartupRegistrationContract.Observe(kind, new(false), Root), kind);
        Assert.True(absent.Available); Assert.False(absent.Registration!.Registered); Assert.Equal("absent", absent.RootAssociation);
        var failed = Read(StartupRegistrationContract.Unavailable(kind, "permission_denied"), kind);
        Assert.False(failed.Available); Assert.Null(failed.ObservedAt); Assert.Null(failed.Registration); Assert.Equal("unknown", failed.RootAssociation);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void ClientRejectsInvalidHeaderBodyTimeAndAssociation(int fault)
    {
        var original = Self();
        var bad = fault switch
        {
            0 => original with { SchemaVersion = 2 },
            1 => original with { Kind = "boot" },
            2 => original with { RegistrationScope = "root-local" },
            3 => original with { RegistrationId = "arbitrary" },
            4 => original with { CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-3) },
            5 => original with { CompletedAt = DateTimeOffset.UtcNow.AddMinutes(3) },
            6 => original with { ObservedAt = null },
            7 => original with { Reason = "permission_denied" },
            8 => original with { Registration = original.Registration! with { Enabled = true } },
            9 => original with { Registration = original.Registration! with { Command = new('x', 8193) } },
            10 => original with { AssociatedRoot = @"D:\forged" },
            _ => original with { RootAssociation = "owner" },
        };
        Assert.Equal("protocol_mismatch", Assert.Throws<AgentCommandException>(() => Read(bad)).Code);
    }

    [Fact]
    public void BootEnabledIsNativeSettingWithNoUserRootAssociation()
    {
        var status = Read(StartupRegistrationContract.Observe("boot", new(true, Enabled: false, State: "disabled", ActionCount: 1,
            ActionType: 0, Executable: @"C:\protected\Agent.exe", Arguments: "--boot --root \"C:\\protected\"", LastTaskResult: 0, MissedRuns: 0), Root), "boot");
        Assert.Equal("machine-global", status.RegistrationScope); Assert.Equal("not-applicable", status.RootAssociation);
        Assert.Null(status.AssociatedRoot); Assert.False(status.Registration!.Enabled);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void BootRequiredCountersAndActionMetadataCannotSilentlyDisappear(int fault)
    {
        var data = new StartupRegistrationData(true, Enabled: false, State: "ready", ActionCount: 1, ActionType: 0,
            Executable: @"C:\protected\Agent.exe", Arguments: "", LastTaskResult: 0, MissedRuns: 0);
        var original = StartupRegistrationContract.Observe("boot", data, Root);
        var invalid = fault switch
        {
            0 => data with { LastTaskResult = null },
            1 => data with { MissedRuns = null },
            2 => data with { ActionType = null },
            _ => data with { ActionCount = -1 },
        };
        Assert.Throws<AgentCommandException>(() => Read(original with { Registration = invalid }, "boot"));
    }

    [Fact]
    public void MissingAvailableAndFailedObservationWithPayloadAreProtocolErrors()
    {
        var json = JsonSerializer.SerializeToNode(Self(), AgentProtocol.JsonOptions)!.AsObject(); json.Remove("available");
        Assert.Equal("protocol_mismatch", Assert.Throws<AgentCommandException>(() =>
            AgentStartupRegistrationClient.ReadObservation(JsonSerializer.SerializeToElement(json), "self", Root)).Code);
        Assert.Throws<AgentCommandException>(() => Read(Self() with { Available = false, Reason = "permission_denied" }));
    }

    [Fact]
    public void RootComparisonDoesNotFoldCaseSensitiveNamespacesOrEmitBidiAsDisplayText()
    {
        Assert.Equal("other-root-lexical", Read(Self("\"C:\\app\\Vigil.exe\" --background --root \"C:\\OWNED-root\"")).RootAssociation);
        var raw = "\"C:\\app\\Vigil.exe\" --background --root \"C:\\owned\u202E-root\"\n\u001B";
        var status = Self(raw); Assert.Equal("unknown", status.RootAssociation); Assert.Null(status.AssociatedRoot);
        var json = JsonSerializer.Serialize(status, AgentProtocol.JsonOptions);
        Assert.DoesNotContain('\u202E', json); Assert.DoesNotContain('\u001B', json); Assert.DoesNotContain('\n', json);
        Assert.Equal(raw, Read(status).Registration!.Command);
    }

    [Fact]
    public void FullUtf8BudgetFailsClosedRatherThanTruncatingACommandOrRoot()
    {
        var command = "\"C:\\app\\Vigil.exe\" --background --root \"C:\\" + new string('界', 6000) + "\"";
        Assert.Equal("registration_too_large", Assert.Throws<StartupRegistrationSourceException>(() => Self(command)).Reason);
    }
}
