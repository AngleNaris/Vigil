using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Host = SystemAutorun.IntegrationTests.SelfStartupAgentTests.Host;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupWorkflowIntegrationTests
{
    [Theory]
    [InlineData("empty", true, 1)] [InlineData("empty", false, 0)] [InlineData("missing", true, 2)] [InlineData("missing", false, 0)]
    [InlineData("same", true, 0)] [InlineData("same", false, 1)]
    public async Task ExplicitWorkflowPersistsBeforeOneRealAgentJournalAndClearsOnlyFreshAck(string state, bool enabled, int calls)
    {
        await using var host = new Host();
        if (state == "missing") { host.Owned.Native.DeleteRun(); }
        if (state == "same") { host.Owned.Native.Set(1, host.Owned.TargetBytes); }
        var workflow = SelfStartupWorkflow.ForRoot(host.Paths); var before = host.Storage();
        Assert.True((await workflow.OpenAsync()).CanPrepare); var prepared = await workflow.PrepareAsync(enabled);
        Assert.True(prepared.CanConfirm); Assert.False(prepared.Preparation!.Confirmed); AssertStorage(before, host.Storage());
        await Assert.ThrowsAsync<AgentCommandException>(() => workflow.SubmitAsync(prepared.Preparation)); AssertStorage(before, host.Storage());
        // The explicit test/user action, never the coordinator or recovery record,
        // supplies confirmation. Native adapters remain private GUID-only.
        var result = await workflow.SubmitAsync(prepared.Preparation with { Confirmed = true });
        Assert.Equal(SelfStartupWorkflowState.Completed, result.State); Assert.True(result.CanPrepare); Assert.False(prepared.Preparation.Confirmed);
        Assert.False(File.Exists(Path.Combine(host.Paths.RootDirectory, SelfStartupPendingStore.FileName)));
        var receipt = await new AgentOperationClient(host.Client).ReadAsync(result.OperationId!);
        Assert.NotNull(receipt); Assert.Equal(SelfStartupJournal.FixtureCommand, receipt.Command); Assert.Equal(OperationState.Completed, receipt.State);
        Assert.Equal(host.Journal.InstanceId, receipt.OwnerInstanceId); Assert.Equal(result.OperationId, receipt.Response!.OperationId);
        Assert.Equal(calls, Assert.IsType<JsonElement>(receipt.Response.Data).GetProperty("calls").GetArrayLength());
        Assert.Single(Directory.EnumerateFiles(host.Paths.RootDirectory, "*.operation.json", SearchOption.AllDirectories));
        var opens = host.Opens; before = host.Storage();
        await Assert.ThrowsAsync<AgentCommandException>(() => workflow.SubmitAsync(prepared.Preparation with { Confirmed = true }));
        Assert.Equal(opens, host.Opens); AssertStorage(before, host.Storage()); Assert.Equal(result, await workflow.OpenAsync());
        await host.Owned.AssertReleased();
    }

    [Fact]
    public async Task DriftAfterPreparationIsFreshExplicitNotExecutedAndDoesNotWriteOrReplay()
    {
        await using var host = new Host(); var workflow = SelfStartupWorkflow.ForRoot(host.Paths);
        var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        host.Owned.Native.Set(4, [1, 2, 3, 4]); var changed = host.Owned.Native.Read();
        var result = await workflow.SubmitAsync(prepared with { Confirmed = true });
        Assert.Equal(SelfStartupWorkflowState.Refused, result.State); Assert.Equal("not_executed", result.ErrorCode);
        Assert.True(result.CanPrepare); Assert.Equal(changed, host.Owned.Native.Read());
        Assert.False(File.Exists(Path.Combine(host.Paths.RootDirectory, SelfStartupPendingStore.FileName)));
        var receipt = await new AgentOperationClient(host.Client).ReadAsync(result.OperationId!);
        Assert.Equal(OperationState.Completed, receipt!.State); Assert.False(receipt.Response!.Ok); Assert.Equal("not_executed", receipt.Response.ErrorCode);
        await host.Owned.AssertReleased();
    }

    private static void AssertStorage(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    { Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }
}
