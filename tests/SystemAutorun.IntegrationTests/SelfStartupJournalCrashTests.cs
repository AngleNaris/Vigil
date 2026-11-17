using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Startup;
using Xunit;
using Fixture = SystemAutorun.IntegrationTests.SelfStartupPlanningSessionTests.Fixture;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupJournalCrashTests
{
    internal static readonly string[] Stages = ["accepted", "before_native", "key_created", "native_returned", "native_verified", "callback_returning", "receipt_readback", "receipt_verified"];
    public static IEnumerable<object[]> Cases() => Stages.Select(stage => new object[] { stage });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RealOwnerTerminationNeverReexecutesEvenWhenMatchingNativeStateHasNoFinalReceipt(string stage)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); var paths = SelfStartupJournalTests.Paths(owned);
        var envelope = SelfStartupJournalTests.Envelope(owned.Request()); var root = owned.Admission.Root;
        var input = new StartupMutationAdapterTests.SelfJournalWorkerInput(owned.Native.Id, Path.GetDirectoryName(owned.Package.Root)!, envelope);
        File.WriteAllBytes(Path.Combine(root, "self-journal-input.json"), JsonSerializer.SerializeToUtf8Bytes(input, AgentProtocol.JsonOptions));
        var bundle = new StartupMutationAdapterTests.CrashHostBundle(root); var mode = "self-journal-" + stage;
        await using var owner = bundle.Start(mode); await owner.WaitForCheckpointAsync(mode);
        if (stage != "accepted") { await owned.AssertHeld(); }
        var created = stage is not ("accepted" or "before_native"); var registered = created && stage != "key_created";
        Assert.Equal(created, owned.Native.Read().KeyPresent); Assert.Equal(registered, owned.Native.Read().Registration.Registered);
        var stored = SelfStartupJournalTests.ReadReceipt(paths, envelope.OperationId!);
        var completed = stage is "receipt_readback" or "receipt_verified";
        Assert.Equal(completed ? OperationState.Completed : OperationState.Running, stored.State);
        if (!completed) { Assert.Null(stored.Response); }
        else { Assert.True(stored.Response!.Ok); Assert.Equal(2, SelfStartupJournalTests.Data(stored.Response).Calls.Count); }
        await owner.TerminateAsync(); Assert.NotEqual(0, owner.ExitCode); await owned.AssertReleased();
        var before = SelfStartupJournalTests.Images(paths); var raw = owned.Native.Read();
        var readers = new HashSet<int> { owner.Id };
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await using var reader = bundle.Start("self-journal-replay"); using var report = await reader.ReadReportAsync(); var row = report.RootElement;
            Assert.True(readers.Add(row.GetProperty("pid").GetInt32())); Assert.Equal(0, row.GetProperty("factories").GetInt32()); Assert.Equal(0, row.GetProperty("checkpoints").GetInt32());
            var replay = row.GetProperty("replay").Deserialize<AgentResponse>(AgentProtocol.JsonOptions)!; Assert.True(replay.Replayed);
            Assert.Equal(completed, replay.Ok);
            if (!replay.Ok) { Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.Null(replay.Data); }
            Assert.Equal(raw, row.GetProperty("native").Deserialize<SelfStartupRawObservation>(AgentProtocol.JsonOptions));
            SelfStartupJournalTests.AssertImages(before, SelfStartupJournalTests.Images(paths)); Assert.Equal(raw, owned.Native.Read());
        }
        Assert.Equal(stored.State, SelfStartupJournalTests.ReadReceipt(paths, envelope.OperationId!).State);
    }
}
