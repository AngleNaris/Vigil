using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Startup;
using Host = SystemAutorun.IntegrationTests.SelfStartupAgentTests.Host;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupClientTests
{
    [Theory]
    [InlineData("empty", true, 1)] [InlineData("empty", false, 0)] [InlineData("missing", true, 2)] [InlineData("missing", false, 0)]
    [InlineData("same", true, 0)] [InlineData("same", false, 1)]
    public async Task TypedPreparationAndExplicitSubmissionShareOriginalJournalAndNeverAutoConfirm(string state, bool enabled, int calls)
    {
        await using var host = new Host();
        if (state == "missing") { host.Owned.Native.DeleteRun(); }
        if (state == "same") { host.Owned.Native.Set(1, host.Owned.TargetBytes); }
        var client = new AgentSelfStartupClient(host.Client, host.Paths.RootDirectory); var before = host.Storage();
        var prepared = await client.PrepareAsync(enabled); Assert.False(prepared.Confirmed); Assert.Equal(enabled, prepared.Enabled); AssertStorage(before, host.Storage());
        var opens = host.Opens; var id = AgentProtocol.NewOperationId();
        var denied = await Assert.ThrowsAsync<AgentCommandException>(() => client.SubmitAsync(prepared, id));
        Assert.Equal("authorization_required", denied.Code); Assert.Null(denied.OperationId); Assert.Equal(opens, host.Opens); AssertStorage(before, host.Storage());
        // This explicit test/user action, not the client or saved preparation, supplies confirmation.
        var confirmed = prepared with { Confirmed = true }; var response = await client.SubmitAsync(confirmed, id.ToUpperInvariant());
        Assert.True(response.Ok); Assert.False(response.Replayed); Assert.Equal(id, response.OperationId); Assert.False(prepared.Confirmed);
        Assert.Equal(calls, Assert.IsType<JsonElement>(response.Data).GetProperty("calls").GetArrayLength());
        var receipt = await new AgentOperationClient(host.Client).ReadAsync(id);
        // The GUID-only service deliberately journals FixtureCommand, never the production native command.
        Assert.NotNull(receipt); Assert.Equal(SelfStartupJournal.FixtureCommand, receipt.Command); Assert.Equal(OperationState.Completed, receipt.State);
        Assert.Equal(host.Journal.InstanceId, receipt.OwnerInstanceId); Assert.Equal(id, receipt.Response!.OperationId);
        Assert.Single(Directory.EnumerateFiles(host.Paths.RootDirectory, "*.operation.json", SearchOption.AllDirectories));
        opens = host.Opens; var native = host.Owned.Native.Read(); before = host.Storage();
        var replay = await client.SubmitAsync(confirmed, id);
        Assert.True(replay.Ok); Assert.True(replay.Replayed); Assert.Equal(id, replay.OperationId); Assert.Equal(opens, host.Opens);
        Assert.Equal(native, host.Owned.Native.Read()); AssertStorage(before, host.Storage()); await host.Owned.AssertReleased();
    }

    [Fact]
    public async Task TypedClientObservesRunningAndDuplicateWithoutAnotherDispatchOrInternalFailureData()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        await using var host = new Host((owned, token) => { entered.TrySetResult(); release.Wait(token); return owned.Package.Open(token: token); });
        var client = new AgentSelfStartupClient(host.Client, host.Paths.RootDirectory); var id = AgentProtocol.NewOperationId(); var request = host.Owned.Request();
        var pending = client.SubmitAsync(request, id, TimeSpan.FromSeconds(10));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var receipt = await new AgentOperationClient(host.Client).ReadAsync(id); Assert.Equal(OperationState.Running, receipt!.State);
            var duplicate = await client.SubmitAsync(request, id);
            Assert.False(duplicate.Ok); Assert.True(duplicate.Replayed); Assert.Equal("operation_in_progress", duplicate.ErrorCode); Assert.Null(duplicate.Data);
            Assert.Equal(id, duplicate.OperationId); Assert.Equal(1, host.Opens);
        }
        finally { release.Set(); await pending; }
        Assert.True((await pending).Ok); Assert.Equal(OperationState.Completed, (await new AgentOperationClient(host.Client).ReadAsync(id))!.State);
        await host.Owned.AssertReleased();
    }

    [Fact]
    public async Task MismatchedTypedRootCannotMutateEitherLivePrivateServer()
    {
        await using var transport = new Host(); await using var expected = new Host();
        var beforeTransport = transport.Storage(); var beforeExpected = expected.Storage();
        var client = new AgentSelfStartupClient(transport.Client, expected.Paths.RootDirectory);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitAsync(transport.Owned.Request(), AgentProtocol.NewOperationId()));
        Assert.Equal(0, transport.Opens); Assert.Equal(0, expected.Opens);
        AssertStorage(beforeTransport, transport.Storage()); AssertStorage(beforeExpected, expected.Storage());
        Assert.False(transport.Owned.Native.Read().Registration.Registered); Assert.False(expected.Owned.Native.Read().Registration.Registered);
    }

    private static void AssertStorage(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    { Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }
}
