using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    internal sealed record SelfPendingWorkerInput(string OperationId, string NextOperationId, bool Enabled);
    private static async Task<int> RunSelfPendingWorkerAsync(string control, string mode)
    {
        var root = RequireCrashRoot(control); var image = Environment.ProcessPath ?? throw new IOException("No fixture image."); VerifyCrashImage(image, root);
        if (mode is not ("self-pending-flushed" or "self-pending-published" or "self-pending-reserved" or "self-pending-read" or "self-pending-attempt" or "self-pending-new"))
        { throw new ArgumentException("An exact pending fixture mode is required."); }
        var input = JsonSerializer.Deserialize<SelfPendingWorkerInput>(ReadCrashInput(root, "self-pending-input.json", 2048), AgentProtocol.JsonOptions)
            ?? throw new IOException("No owned pending input.");
        _ = OperationJournal.NormalizeId(input.OperationId); _ = OperationJournal.NormalizeId(input.NextOperationId);
        var paths = ProjectPaths.Resolve(root); Console.OutputEncoding = new UTF8Encoding(false); Console.InputEncoding = new UTF8Encoding(false);
        if (mode == "self-pending-read")
        {
            var snapshot = await new SelfStartupPendingStore(paths).ReadAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, snapshot }, AgentProtocol.JsonOptions)); return 0;
        }
        if (mode == "self-pending-attempt")
        {
            string? code = null;
            try { _ = await new SelfStartupPendingStore(paths).ReserveAsync(input.NextOperationId, !input.Enabled, leaseTimeout: TimeSpan.FromMilliseconds(75)); }
            catch (SelfStartupPendingStoreException error) { code = error.Code; }
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, code }, AgentProtocol.JsonOptions)); return 0;
        }
        if (mode == "self-pending-new")
        {
            _ = await new SelfStartupPendingStore(paths).ReserveAsync(input.NextOperationId, !input.Enabled);
            var snapshot = await new SelfStartupPendingStore(paths).ReadAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, snapshot }, AgentProtocol.JsonOptions)); return 0;
        }
        void Pause(string stage)
        {
            if (mode != "self-pending-" + stage) { return; }
            var ready = Path.Combine(root, "history-crash.ready.json"); var staging = ready + ".staging";
            File.WriteAllText(staging, JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, stage = mode }), new UTF8Encoding(false)); File.Move(staging, ready);
            using var terminatedByParent = new ManualResetEventSlim();
            if (!terminatedByParent.Wait(TimeSpan.FromSeconds(40))) { throw new TimeoutException("Owned pending worker was not terminated."); }
            throw new InvalidOperationException("A pending crash checkpoint must never resume.");
        }
        // Test-only reflection keeps a deterministic IO/crash seam out of the public API.
        var store = (SelfStartupPendingStore)typeof(SelfStartupPendingStore).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(ProjectPaths), typeof(Action<string>)], null)!.Invoke([paths, (Action<string>)Pause]);
        _ = await store.ReserveAsync(input.OperationId, input.Enabled); Pause("reserved");
        throw new InvalidOperationException("Pending worker did not reach its requested boundary.");
    }
}
