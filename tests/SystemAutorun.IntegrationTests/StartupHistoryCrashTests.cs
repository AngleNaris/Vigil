using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    public static IEnumerable<object[]> HistoryCrashCases()
    {
        foreach (var source in new[] { "Startup", "Common Startup" })
        foreach (var stage in CrashBoundaries)
        { yield return [source, stage, stage is not ("intent_verified" or "before_native"), stage is not ("intent_verified" or "before_native" or "native_renamed"), stage is "commit_index_published" or "commit_verified"]; }
    }

    [Theory]
    [MemberData(nameof(HistoryCrashCases))]
    public async Task HistoryCrashOwnerTerminationKeepsPersistentFactsAndFreshQueriesReadOnly(string source, string stage, bool moved, bool ledger, bool committed)
    {
        using var store = HistoryTransactionStore(source); var root = store.History.Owned.Root;
        await store.History.Owned.CreateLinkAsync(source); var candidate = store.History.Payload; byte[] original;
        using (var captureManager = store.History.Owned.Manager())
        using (var capture = captureManager.CaptureShortcutForDisable(source, "owned"))
        {
            original = capture.CopyOriginalBytes(); var input = capture.Input;
            candidate = candidate with { Binding = candidate.Binding with { Original = new(input.FileIdentity, input.Length, input.LinkCount, input.Sha256) } };
        }
        File.WriteAllBytes(Path.Combine(root, "history-crash-candidate.json"), StartupHistoryFormat.Write(candidate));
        File.WriteAllBytes(Path.Combine(root, "history-crash-record.json"), StartupBackupLedger.Write([store.History.Record]));
        var bundle = new CrashHostBundle(root);
        await using var owner = bundle.Start(stage); await owner.WaitForCheckpointAsync(stage);
        await using (var busy = bundle.Start("read"))
        {
            using var report = await busy.ReadReportAsync(); var metadata = report.RootElement.GetProperty("metadata");
            Assert.Equal("busy", metadata.GetProperty("State").GetString()); Assert.Equal(0, report.RootElement.GetProperty("directoryCalls").GetInt32());
            Assert.Equal("unobserved", report.RootElement.GetProperty("native").GetProperty("State").GetString());
        }
        await owner.TerminateAsync(); Assert.NotEqual(0, owner.ExitCode);
        Assert.Equal(!moved, File.Exists(store.History.Record.Origin)); Assert.Equal(moved, File.Exists(store.History.Record.BackupPath));
        Assert.Equal(ledger, File.Exists(store.History.Owned.Paths.StartupBackupPath)); Assert.True(File.Exists(IntentPath(store)));
        Assert.Equal(original, File.ReadAllBytes(moved ? store.History.Record.BackupPath! : store.History.Record.Origin));
        var orphanProof = stage is "commit_proof_published" or "commit_index_published" or "commit_verified";
        Assert.Equal(orphanProof, File.Exists(CommittedHistoryPath(store)));
        if (stage == "commit_proof_flushed") { Assert.True(Directory.GetFiles(store.DirectoryPath).Length > 2); }
        var before = CrashDataImages(store); var readerIds = new HashSet<int> { owner.Id };
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await using var reader = bundle.Start("read"); using var report = await reader.ReadReportAsync(); var row = report.RootElement;
            Assert.True(readerIds.Add(row.GetProperty("pid").GetInt32())); Assert.Equal(root, row.GetProperty("root").GetString());
            var metadata = row.GetProperty("metadata").Deserialize<StartupHistoryIntentObservation>()!;
            var native = row.GetProperty("native").Deserialize<StartupHistoryNativeCheck>()!;
            Assert.Equal("observed_pending", metadata.State); Assert.Equal("matched_image", metadata.ConfigurationImage);
            Assert.Equal(ledger ? "proposed_image" : "declared_empty", metadata.LedgerImage);
            Assert.Equal(committed ? "protected_committed" : "candidate", metadata.HistoryState);
            Assert.Equal(moved ? "archive_matches" : "original_matches", native.State);
            AssertReconciliationNoAuthority(new(metadata, native));
            foreach (var axis in new[] { "metadata", "native" })
            foreach (var flag in new[] { "NativeWriteReady", "ExecutionRequested", "TransactionFinalized", "MayFinalize" })
            { Assert.False(row.GetProperty(axis).GetProperty(flag).GetBoolean()); }
            AssertCrashDataImagesUnchanged(store, before);
        }
        using var manager = store.History.Owned.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(store.History.Record.Id));
        AssertCrashDataImagesUnchanged(store, before); Assert.True(File.Exists(IntentPath(store)));
        Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
    }

    private static Dictionary<string, byte[]> CrashDataImages(HistoryStoreFixture store)
    {
        var root = store.History.Owned.Root; var excluded = Path.Combine(root, "history-crash-host") + Path.DirectorySeparatorChar;
        var paths = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.StartsWith(excluded, StringComparison.Ordinal))
            .Concat(Directory.GetFiles(store.DirectoryPath, "*", SearchOption.AllDirectories));
        return paths.Order().ToDictionary(path => path, File.ReadAllBytes);
    }
    private static void AssertCrashDataImagesUnchanged(HistoryStoreFixture store, Dictionary<string, byte[]> before)
    {
        var after = CrashDataImages(store); Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); }
    }

    internal sealed class CrashHostBundle
    {
        private readonly string _root;
        private readonly string _directory;
        private readonly string _apphost;
        internal CrashHostBundle(string root)
        {
            _root = RequireCrashRoot(root); _directory = Path.Combine(root, "history-crash-host");
            var assembly = typeof(StartupMutationAdapterTests).Assembly.Location; var output = Path.GetDirectoryName(assembly)!;
            Assert.Contains(Path.Combine("tests", "SystemAutorun.IntegrationTests", "bin") + Path.DirectorySeparatorChar, output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            _apphost = Path.ChangeExtension(assembly, ".exe"); Assert.True(File.Exists(_apphost));
            Directory.CreateDirectory(_directory);
            foreach (var file in Directory.GetFiles(output).Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
            { File.Copy(file, Path.Combine(_directory, Path.GetFileName(file))); }
        }
        internal CrashProcess Start(string mode)
        {
            var executable = Path.Combine(_directory, "SAFixture_R46_" + Guid.NewGuid().ToString("N") + ".exe");
            VerifyCrashImage(executable, _root); File.Copy(_apphost, executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "--owned-history-fixture", _root, mode }) { start.ArgumentList.Add(argument); }
            return new(_root, executable, Process.Start(start) ?? throw new InvalidOperationException("Owned crash host did not start."));
        }
    }
    private static void VerifyCrashImage(string image, string root)
    {
        Assert.Equal(Path.GetFullPath(image), image); Assert.Equal(Path.Combine(RequireCrashRoot(root), "history-crash-host"), Path.GetDirectoryName(image));
        var name = Path.GetFileNameWithoutExtension(image); const string prefix = "SAFixture_R46_";
        Assert.StartsWith(prefix, name, StringComparison.Ordinal); Assert.True(Guid.TryParseExact(name[prefix.Length..], "N", out _));
    }
    internal sealed class CrashProcess : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _image;
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        internal int Id => _process.Id;
        internal int ExitCode => _process.ExitCode;
        internal CrashProcess(string root, string image, Process process)
        { _root = root; _image = image; _process = process; _stdout = process.StandardOutput.ReadToEndAsync(); _stderr = process.StandardError.ReadToEndAsync(); }
        private void RequireOwnedLiveProcess()
        {
            VerifyCrashImage(_image, _root);
            try
            {
                var module = _process.MainModule;
                if (module is null && _process.HasExited) { return; }
                Assert.NotNull(module); Assert.Equal(_image, Path.GetFullPath(module.FileName), StringComparer.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException) when (_process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (_process.HasExited) { }
        }
        internal async Task WaitForCheckpointAsync(string expected)
        {
            var marker = Path.Combine(_root, "history-crash.ready.json"); var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (_process.HasExited) { throw new InvalidOperationException("Crash host exited before checkpoint: " + await _stderr); }
                if (File.Exists(marker))
                {
                    using var ready = JsonDocument.Parse(await File.ReadAllBytesAsync(marker)); var value = ready.RootElement;
                    Assert.Equal(Id, value.GetProperty("pid").GetInt32()); Assert.Equal(_root, value.GetProperty("root").GetString());
                    Assert.Equal(expected, value.GetProperty("stage").GetString()); RequireOwnedLiveProcess(); return;
                }
                await Task.Delay(20);
            }
            throw new TimeoutException("Owned crash host did not reach " + expected);
        }
        internal async Task<JsonDocument> ReadReportAsync()
        {
            // Short readers may finish before MainModule can be enumerated. Join the
            // exact created Process, then verify its report PID and owned image instead.
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ExitCode == 0, "Owned reader failed: " + await _stderr);
            var report = JsonDocument.Parse(await _stdout); Assert.Equal(Id, report.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(_image, report.RootElement.GetProperty("image").GetString()); return report;
        }
        internal async Task TerminateAsync()
        {
            if (!_process.HasExited)
            {
                RequireOwnedLiveProcess();
                try { _process.Kill(); } catch (InvalidOperationException) when (_process.HasExited) { }
            }
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async ValueTask DisposeAsync() { try { await TerminateAsync(); } finally { _process.Dispose(); } }
    }
}
