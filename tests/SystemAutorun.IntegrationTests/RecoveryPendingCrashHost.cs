using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    internal sealed record RecoveryPendingWorkerInput(string OperationId, string NextOperationId, string ExpectedRevision, string TaskName);
    private static async Task<int> RunRecoveryPendingWorkerAsync(string control, string mode)
    {
        var root = RequireCrashRoot(control); var image = Environment.ProcessPath ?? throw new IOException("No fixture image."); VerifyCrashImage(image, root);
        if (mode is not ("recovery-pending-flushed" or "recovery-pending-published" or "recovery-pending-reserved"
            or "recovery-pending-read" or "recovery-pending-attempt" or "recovery-pending-new" or "recovery-pending-other"))
        { throw new ArgumentException("An exact recovery pending fixture mode is required."); }
        var input = JsonSerializer.Deserialize<RecoveryPendingWorkerInput>(ReadCrashInput(root, "recovery-pending-input.json", 4096), AgentProtocol.JsonOptions)
            ?? throw new IOException("No owned pending input.");
        _ = OperationJournal.NormalizeId(input.OperationId); _ = OperationJournal.NormalizeId(input.NextOperationId);
        var paths = ProjectPaths.Resolve(root); Console.OutputEncoding = new UTF8Encoding(false); Console.InputEncoding = new UTF8Encoding(false);
        var taskName = mode == "recovery-pending-other" ? "another" : input.TaskName;
        var ordinary = new RecoveryPendingStore(paths, taskName);
        if (mode == "recovery-pending-read")
        {
            var snapshot = await ordinary.ReadAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, snapshot }, AgentProtocol.JsonOptions)); return 0;
        }
        if (mode == "recovery-pending-attempt")
        {
            string? code = null;
            try { _ = await ordinary.ReserveAsync(input.NextOperationId, input.ExpectedRevision, leaseTimeout: TimeSpan.FromMilliseconds(75)); }
            catch (RecoveryPendingStoreException error) { code = error.Code; }
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, code }, AgentProtocol.JsonOptions)); return 0;
        }
        if (mode is "recovery-pending-new" or "recovery-pending-other")
        {
            _ = await ordinary.ReserveAsync(mode == "recovery-pending-other" ? AgentProtocol.NewOperationId() : input.NextOperationId, input.ExpectedRevision);
            var snapshot = await ordinary.ReadAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, snapshot }, AgentProtocol.JsonOptions)); return 0;
        }
        void Pause(string stage)
        {
            if (mode != "recovery-pending-" + stage) { return; }
            var ready = Path.Combine(root, "history-crash.ready.json"); var staging = ready + ".staging";
            File.WriteAllText(staging, JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, stage = mode }), new UTF8Encoding(false)); File.Move(staging, ready);
            using var terminatedByParent = new ManualResetEventSlim();
            if (!terminatedByParent.Wait(TimeSpan.FromSeconds(40))) { throw new TimeoutException("Owned pending worker was not terminated."); }
            throw new InvalidOperationException("A pending crash checkpoint must never resume.");
        }
        var store = (RecoveryPendingStore)typeof(RecoveryPendingStore).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(ProjectPaths), typeof(string), typeof(Action<string>)], null)!.Invoke([paths, input.TaskName, (Action<string>)Pause]);
        _ = await store.ReserveAsync(input.OperationId, input.ExpectedRevision); Pause("reserved");
        throw new InvalidOperationException("Pending worker did not reach the requested boundary.");
    }
}
