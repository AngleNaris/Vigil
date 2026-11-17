using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    // Test-assembly entry point only. VSTest still discovers this same assembly;
    // neither the production Console nor Agent receives a fixture command.
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--r121-resource-gui")
            { return await R121ResourceGuiHost.RunAsync(args[1], args[2]); }
            if (args.Length == 3 && args[0] == "--r121-passive-worker")
            { return await RunR121PassiveWorkerAsync(args[1], args[2]); }
            if (args.Length == 3 && args[0] == "--owned-history-fixture" && args[2].StartsWith("recovery-pending-", StringComparison.Ordinal))
            { return await RunRecoveryPendingWorkerAsync(args[1], args[2]); }
            if (args.Length == 3 && args[0] == "--owned-history-fixture" && args[2].StartsWith("self-pending-", StringComparison.Ordinal))
            { return await RunSelfPendingWorkerAsync(args[1], args[2]); }
            if (args.Length == 3 && args[0] == "--owned-history-fixture" && args[2].StartsWith("self-journal-", StringComparison.Ordinal))
            { return await RunSelfJournalWorkerAsync(args[1], args[2]); }
            if (args.Length == 3 && args[0] == "--owned-history-fixture" && args[2] is "self-admission-hold" or "self-admission-probe" or "self-admission-scope")
            { return await RunSelfAdmissionWorkerAsync(args[1], args[2]); }
            if (args.Length != 3 || args[0] != "--owned-history-fixture" || (args[2] is not ("read" or "elevated-denied") && !CrashBoundaries.Contains(args[2])))
            { throw new ArgumentException("An explicit owned history fixture operation is required."); }
            var root = RequireCrashRoot(args[1]); var image = Environment.ProcessPath ?? throw new IOException("No fixture image.");
            if (Path.GetDirectoryName(image) != Path.Combine(root, "history-crash-host") || !Path.GetFileName(image).StartsWith("SAFixture_R46_", StringComparison.Ordinal))
            { throw new InvalidOperationException("Only an independently named temporary fixture image may run this entry point."); }
            Console.OutputEncoding = new UTF8Encoding(false); Console.InputEncoding = new UTF8Encoding(false);
            if (args[2] == "elevated-denied")
            {
                var requested = JsonSerializer.Deserialize<string[]>(ReadCrashInput(root, "elevated-denied-request.json", 8192))
                    ?? throw new IOException("No rejected-command fixture request.");
                if (requested.Length == 0 || requested[0] is not ("startup-disable" or "startup-restore" or "startup-rebuild"))
                { throw new InvalidOperationException("The fixture may invoke only retired startup commands, never a boot task."); }
                var assemblyPath = typeof(StartupMutationAdapterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(attribute => attribute.Key == "ElevatedAssemblyPath").Value!;
                assemblyPath = Path.GetFullPath(assemblyPath);
                if (!assemblyPath.Contains(Path.Combine("src", "SystemAutorun.Elevated", "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(assemblyPath) != "Vigil.Elevated.dll") { throw new IOException("Unexpected owned Helper build."); }
                var assembly = Assembly.LoadFrom(assemblyPath); var entry = assembly.EntryPoint ?? throw new IOException("No managed Helper entry point.");
                var value = entry.Invoke(null, [requested]);
                var exit = value is Task<int> task ? await task : value is int code ? code : throw new IOException("Unexpected Helper exit type.");
                Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, helperExitCode = exit })); return 0;
            }
            var history = Path.Combine(root + ".history-state.container", "store"); var native = new CrashNativeAccess(root);
            if (args[2] == "read")
            {
                var observation = await new StartupHistoryIntentReader(root, () => history).InspectNativeAsync(native);
                Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, directoryCalls = native.DirectoryCalls,
                    metadata = observation.Metadata, native = observation.Native }));
                return 0;
            }
            var candidate = StartupHistoryFormat.Read(ReadCrashInput(root, "history-crash-candidate.json", StartupHistoryFormat.MaximumBytes));
            var record = StartupBackupLedger.Read(ReadCrashInput(root, "history-crash-record.json", StartupBackupLedger.MaximumBytes), requireComplete: true).Records.Single();
            void Pause(string stage)
            {
                if (stage != args[2]) { return; }
                var ready = Path.Combine(root, "history-crash.ready.json"); var staging = ready + ".staging";
                File.WriteAllText(staging, JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, stage }), new UTF8Encoding(false));
                File.Move(staging, ready);
                using var neverReleased = new ManualResetEventSlim(false); neverReleased.Wait();
                throw new InvalidOperationException("A crash checkpoint must be terminated, never resumed.");
            }
            using var manager = new StartupManager(new ProjectPaths(root), native);
            await using var owner = await manager.PrepareHistoryDisableAsync(candidate, record, () => history, checkpoint: Pause);
            var ledger = await owner.ArchiveAndPublishLedgerAsync(checkpoint: Pause);
            if (!ledger.LedgerVerified) { throw new InvalidOperationException("Owned ledger did not verify: " + ledger.Reason); }
            var committed = await owner.PublishCommittedHistoryAsync(checkpoint: Pause);
            throw new InvalidOperationException("Requested crash checkpoint was not reached: " + args[2] + "/" + committed.State);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static readonly string[] CrashBoundaries = ["intent_verified", "before_native", "native_renamed", "ledger_published",
        "commit_proof_flushed", "commit_proof_published", "commit_index_published", "commit_verified"];

    private static string RequireCrashRoot(string root)
    {
        const string prefix = "SystemAutorun-startup-mutation-"; var full = Path.GetFullPath(root); var name = Path.GetFileName(full);
        if (root != full || Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)
            || (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        { throw new InvalidOperationException("A canonical ordinary owned temporary root is required."); }
        return full;
    }
    private static byte[] ReadCrashInput(string root, string leaf, int maximum)
    {
        var path = Path.Combine(root, leaf); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 || input.Length > maximum) { throw new IOException("Fixture input exceeds its limit."); }
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private sealed class CrashNativeAccess(string root) : IStartupNativeAccess
    {
        internal int DirectoryCalls { get; private set; }
        public bool IsAdministrator => true; // Only the owned common-startup fixture, never Windows' real directory.
        public string GetShortcutDirectory(string source)
        {
            DirectoryCalls++;
            return Path.Combine(root, source switch { "Startup" => "user-startup", "Common Startup" => "common-startup",
                _ => throw new ArgumentOutOfRangeException(nameof(source)) });
        }
        public RegistryKey? OpenRunKey(string source, bool writable, bool create) => throw new InvalidOperationException("No registry IO in the history crash host.");
    }
}
