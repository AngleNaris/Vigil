using System.IO;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    internal sealed record SelfJournalWorkerInput(Guid NativeId, string PackageBase, AgentRequest Envelope);
    private static async Task<int> RunSelfJournalWorkerAsync(string control, string mode)
    {
        var root = RequireCrashRoot(control); var image = Environment.ProcessPath ?? throw new IOException("No fixture image.");
        VerifyCrashImage(image, root);
        if (mode != "self-journal-replay" && !SelfStartupJournalCrashTests.Stages.Contains(mode["self-journal-".Length..], StringComparer.Ordinal))
        { throw new ArgumentException("An exact self Journal boundary is required."); }
        var input = JsonSerializer.Deserialize<SelfJournalWorkerInput>(ReadCrashInput(root, "self-journal-input.json", 65536), AgentProtocol.JsonOptions)
            ?? throw new IOException("No owned self Journal input.");
        var packageBase = RequireSelfJournalPackageBase(input.PackageBase);
        if (input.NativeId == Guid.Empty) { throw new IOException("No private native GUID."); }
        var paths = new ProjectPaths(Path.Combine(packageBase, "root"));
        Console.OutputEncoding = new UTF8Encoding(false); Console.InputEncoding = new UTF8Encoding(false);
        if (mode == "self-journal-replay")
        {
            var factories = 0; var checkpoints = 0;
            var replayOnly = new SelfStartupJournal(input.NativeId, paths,
                _ => { factories++; throw new IOException("A replay must not reopen the target."); },
                _ => { factories++; throw new IOException("A replay must not reacquire native admission."); });
            var replay = await replayOnly.RunFixtureAsync(input.Envelope, (_, _) => checkpoints++);
            var native = StartupNativeSlotProbe.ReadSelfNativeFixture(input.NativeId);
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, replay, factories, checkpoints, native }, AgentProtocol.JsonOptions));
            return 0;
        }
        void Pause(string stage)
        {
            if (mode != "self-journal-" + stage) { return; }
            var ready = Path.Combine(root, "history-crash.ready.json"); var staging = ready + ".staging";
            File.WriteAllText(staging, JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, stage = mode }), new UTF8Encoding(false));
            File.Move(staging, ready);
            using var terminatedByParent = new ManualResetEventSlim();
            if (!terminatedByParent.Wait(TimeSpan.FromSeconds(40))) { throw new TimeoutException("Owned self crash worker was not terminated."); }
            throw new InvalidOperationException("A crash checkpoint must never resume.");
        }
        var runtime = Path.Combine(packageBase, "package", "runtime");
        var coordinator = new SelfStartupJournal(input.NativeId, paths,
            ct => StartupNativeSlotProbe.SelfStartupTargetLease.Open(paths.RootDirectory, Path.Combine(runtime, "Vigil.Agent.exe"), runtime,
                Path.Combine(runtime, "SystemAutorun.Windows.dll"), StartupHistoryProtection.Actor, ct),
            ct => StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(() => new(StartupHistoryProtection.Actor(), root), ct));
        var response = await coordinator.RunFixtureAsync(input.Envelope, (stage, _) => Pause(stage));
        throw new InvalidOperationException("Self worker never reached its requested boundary: " + mode + "/" + response.ErrorCode);
    }

    private static string RequireSelfJournalPackageBase(string path)
    {
        const string prefix = "SystemAutorun-self-target-";
        var full = Path.GetFullPath(path); var leaf = Path.GetFileName(full);
        if (path != full || Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !leaf.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(leaf[prefix.Length..], "N", out var id) || id == Guid.Empty
            || (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        { throw new InvalidOperationException("An ordinary synthetic package fixture is required."); }
        return full;
    }
}
