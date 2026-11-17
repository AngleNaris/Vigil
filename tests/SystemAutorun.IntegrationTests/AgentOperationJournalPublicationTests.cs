using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentOperationJournalPublicationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.Read | FileShare.Delete)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public async Task UnchangedMetadataIsNotRepublishedAndDoesNotBlockNewAdmission(FileShare sharing)
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths);
        var first = Request(); await journal.RunAsync(first, _ => Task.FromResult(new AgentResponse(first.Id, true)));
        using var reader = new FileStream(root.MetadataPath, FileMode.Open, FileAccess.Read, sharing);
        var original = await File.ReadAllBytesAsync(root.MetadataPath);
        var written = File.GetLastWriteTimeUtc(root.MetadataPath);
        var requests = Enumerable.Range(0, 40).Select(_ => Request()).ToArray(); var calls = 0;
        try
        {
            foreach (var request in requests)
            {
                var result = await journal.RunAsync(request, _ => Task.FromResult(new AgentResponse(request.Id, true, ++calls)));
                Assert.True(result.Ok);
            }
            Assert.Equal(40, calls);
        }
        catch (Exception error) { Report(root.Root, error); throw; }
        var old = new byte[reader.Length]; reader.ReadExactly(old); Assert.Equal(original, old);
        Assert.Equal(original, await File.ReadAllBytesAsync(root.MetadataPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(root.MetadataPath));
        Assert.True((await new OperationJournal(root.Paths).RunAsync(requests[^1], _ => throw new InvalidOperationException("Must not execute twice"))).Replayed);
        root.AssertNoCandidates();
    }

    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.Read | FileShare.Delete)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public async Task HeldReceiptAfterExecutionRemainsUnknownAndSameIntentNeverRunsAgain(FileShare sharing)
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths); var request = Request(); var calls = 0;
        FileStream? reader = null;
        try
        {
            var error = await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => journal.RunAsync(request, _ =>
            {
                Assert.True(File.Exists(root.MetadataPath));
                calls++; reader = new FileStream(root.ReceiptPath(request.OperationId!), FileMode.Open, FileAccess.Read, sharing);
                return Task.FromResult(new AgentResponse(request.Id, true, calls));
            }));
            Report(root.Root, error);
            Assert.Equal("publication", error.InnerException!.Data["OperationJournal.Stage"]);
            Assert.Equal(root.ReceiptPath(request.OperationId!), error.InnerException.Data["OperationJournal.Path"]);
            Assert.Equal(1, calls);
            var old = await JsonSerializer.DeserializeAsync<OperationReceipt>(reader!, AgentProtocol.JsonOptions);
            Assert.Equal(OperationState.Running, old!.State);
        }
        finally { if (reader is not null) { await reader.DisposeAsync(); } }
        Assert.Equal(OperationState.Unknown, (await journal.GetAsync(request.OperationId!))!.State);
        var replay = await new OperationJournal(root.Paths).RunAsync(request, _ => throw new InvalidOperationException("Unknown outcome must never execute again"));
        Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.Equal(1, calls); root.AssertNoCandidates();
    }

    [Fact]
    public async Task AdvancingWatermarkStillRefusesBeforePruningOrExecutionAndReportsTheActualHolder()
    {
        using var root = new OwnedRoot(); var clock = new TestTime { Now = DateTimeOffset.UtcNow.AddDays(-8) };
        var journal = new OperationJournal(root.Paths, clock); var first = Request(clock.Now);
        await journal.RunAsync(first, _ => Task.FromResult(new AgentResponse(first.Id, true)));
        var bytes = await File.ReadAllBytesAsync(root.MetadataPath);
        var oldReceipt = await File.ReadAllBytesAsync(root.ReceiptPath(first.OperationId!));
        using var reader = new FileStream(root.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        clock.Now = clock.Now.AddDays(8); var request = Request(clock.Now); var calls = 0;
        var error = await Record.ExceptionAsync(() => journal.RunAsync(request, _ => Task.FromResult(new AgentResponse(request.Id, true, ++calls))));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Report(root.Root, error!);
        Assert.Equal("publication", error!.Data["OperationJournal.Stage"]);
        Assert.Equal(root.MetadataPath, error.Data["OperationJournal.Path"]);
        var holders = JournalFileHolderProbe.Capture(root.Root, root.MetadataPath);
        output.WriteLine(holders.ToString()); Assert.Equal(0u, holders.Error); Assert.Equal(0u, holders.EndError);
        Assert.Contains(Environment.ProcessId, holders.ProcessIds);
        Assert.Equal(0, calls); Assert.Null(await journal.GetAsync(request.OperationId!));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(root.MetadataPath));
        Assert.Equal(oldReceipt, await File.ReadAllBytesAsync(root.ReceiptPath(first.OperationId!))); root.AssertNoCandidates();
        reader.Dispose();
        Assert.True((await journal.RunAsync(request, _ => Task.FromResult(new AgentResponse(request.Id, true, ++calls)))).Ok);
        Assert.Equal(1, calls); Assert.False(File.Exists(root.ReceiptPath(first.OperationId!)));
        using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(root.MetadataPath));
        Assert.Equal(Convert.ToInt64(first.OperationId![..12], 16), metadata.RootElement.GetProperty("minimumTimestamp").GetInt64());
        clock.Now = clock.Now.AddDays(-8);
        var expired = await Assert.ThrowsAsync<AgentCommandException>(() => new OperationJournal(root.Paths, clock)
            .RunAsync(first, _ => throw new InvalidOperationException("Pruned intent must not execute after a clock rewind")));
        Assert.Equal("operation_expired", expired.Code); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredResidueAlreadyCoveredByTheWatermarkPrunesWithoutRepublishingIt()
    {
        using var root = new OwnedRoot(); var clock = new TestTime { Now = DateTimeOffset.UtcNow.AddDays(-8) };
        var journal = new OperationJournal(root.Paths, clock); var first = Request(clock.Now);
        await journal.RunAsync(first, _ => Task.FromResult(new AgentResponse(first.Id, true)));
        var residue = await File.ReadAllBytesAsync(root.ReceiptPath(first.OperationId!));
        clock.Now = clock.Now.AddDays(8); var second = Request(clock.Now);
        await journal.RunAsync(second, _ => Task.FromResult(new AgentResponse(second.Id, true)));
        // Synthetic crash residue: publication succeeded but an expired receipt survived cleanup.
        await File.WriteAllBytesAsync(root.ReceiptPath(first.OperationId!), residue);
        var bytes = await File.ReadAllBytesAsync(root.MetadataPath);
        using var reader = new FileStream(root.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var third = Request(clock.Now);
        Assert.True((await new OperationJournal(root.Paths, clock).RunAsync(third, _ => Task.FromResult(new AgentResponse(third.Id, true)))).Ok);
        Assert.False(File.Exists(root.ReceiptPath(first.OperationId!)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(root.MetadataPath)); root.AssertNoCandidates();
    }

    [Theory]
    [InlineData(-604800000L, "operation_expired")]
    [InlineData(-604799999L, null)]
    [InlineData(300000L, null)]
    [InlineData(300001L, "invalid_operation_id")]
    public async Task RetirementAndFutureAdmissionBoundariesRemainUnchanged(long offsetMilliseconds, string? errorCode)
    {
        using var root = new OwnedRoot();
        var clock = new TestTime { Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) };
        var journal = new OperationJournal(root.Paths, clock); var request = Request(clock.Now.AddMilliseconds(offsetMilliseconds)); var calls = 0;
        Task<AgentResponse> Execute(CancellationToken _) => Task.FromResult(new AgentResponse(request.Id, true, ++calls));
        if (errorCode is null) { Assert.True((await journal.RunAsync(request, Execute)).Ok); Assert.Equal(1, calls); root.AssertNoCandidates(); }
        else
        {
            var error = await Assert.ThrowsAsync<AgentCommandException>(() => journal.RunAsync(request, Execute));
            Assert.Equal(errorCode, error.Code); Assert.Equal(0, calls); Assert.False(File.Exists(root.ReceiptPath(request.OperationId!)));
        }
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(123L)]
    public async Task ValidLegacyWatermarkIsRecognizedAsPersistedAndNotRewritten(long retired)
    {
        using var root = new OwnedRoot(); Directory.CreateDirectory(Path.GetDirectoryName(root.MetadataPath)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"minimumTimestamp\":{retired}}}");
        await File.WriteAllBytesAsync(root.MetadataPath, bytes);
        using var reader = new FileStream(root.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var journal = new OperationJournal(root.Paths); var request = Request();
        Assert.Null(await journal.GetAsync(request.OperationId!));
        Assert.True((await journal.RunAsync(request, _ => Task.FromResult(new AgentResponse(request.Id, true)))).Ok);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(root.MetadataPath)); root.AssertNoCandidates();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":2,\"minimumTimestamp\":0}")]
    public async Task MissingWithReceiptsMalformedOrUnsupportedMetadataNeverBecomesBootstrap(string? metadata)
    {
        using var root = new OwnedRoot(); Directory.CreateDirectory(Path.GetDirectoryName(root.MetadataPath)!);
        await File.WriteAllTextAsync(root.ReceiptPath(Guid.CreateVersion7().ToString("N")), "{}");
        if (metadata is not null) { await File.WriteAllTextAsync(root.MetadataPath, metadata); }
        var journal = new OperationJournal(root.Paths); var request = Request();
        var readError = await Assert.ThrowsAsync<AgentCommandException>(() => journal.GetAsync(request.OperationId!));
        var writeError = await Assert.ThrowsAsync<AgentCommandException>(() => journal.RunAsync(request,
            _ => throw new InvalidOperationException("Invalid metadata must never authorize execution")));
        Assert.Equal("operation_store_corrupt", readError.Code); Assert.Equal("operation_store_corrupt", writeError.Code);
        Assert.False(File.Exists(root.ReceiptPath(request.OperationId!))); root.AssertNoCandidates();
    }

    [Fact]
    public async Task FirstIntentCreatesDurableMetadataBeforePendingExecutionAndStatusDoesNotBootstrap()
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths); var request = Request(); var calls = 0;
        Assert.Null(await journal.GetAsync(request.OperationId!)); Assert.False(File.Exists(root.MetadataPath));
        var result = await journal.RunAsync(request, _ =>
        {
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(root.MetadataPath));
            Assert.Equal(1, metadata.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(long.MinValue, metadata.RootElement.GetProperty("minimumTimestamp").GetInt64());
            using var pending = JsonDocument.Parse(File.ReadAllBytes(root.ReceiptPath(request.OperationId!)));
            Assert.Equal("running", pending.RootElement.GetProperty("state").GetString());
            return Task.FromResult(new AgentResponse(request.Id, true, ++calls));
        });
        Assert.True(result.Ok); Assert.Equal(1, calls);
        Assert.True((await new OperationJournal(root.Paths).RunAsync(request, _ => throw new InvalidOperationException("Must not execute twice"))).Replayed);
        root.AssertNoCandidates();
    }

    [Fact]
    public async Task RepeatedOwnedRevisionCursorsRetainPublicationDiagnosticsOnAnyRealFailure()
    {
        for (var pass = 0; pass < 8; pass++)
        {
            string? owned = null;
            await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(errorObserver: (_, error) =>
            { if (owned is not null) { Report(owned, error); } });
            owned = fixture.Root;
            var client = new AgentConfigurationClient(fixture.Client); _ = await client.ReadAsync();
            var writes = Enumerable.Range(0, 40).Select(index => client.MutateAsync<ApplicationSettings>("settings-patch",
                payload: new ApplicationSettingsPatch(AutoStartMissingOnLaunch: index % 2 != 0))).ToArray();
            var results = await Task.WhenAll(writes);
            var current = await client.ReadAsync();
            Assert.True(current.Preferences.AutoStartMissingOnLaunch); Assert.Equal(results[^1].Revision, current.Revision);
        }
    }

    private void Report(string ownedRoot, Exception error)
    {
        output.WriteLine($"Journal error: pid={Environment.ProcessId}, type={error.GetType().Name}, hresult=0x{error.HResult:X8}: {error}");
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current.Data["OperationJournal.Path"] is not string path) { continue; }
            output.WriteLine($"stage={current.Data["OperationJournal.Stage"]}, destination={path}, temporary={current.Data["OperationJournal.TemporaryPath"]}");
            try { output.WriteLine($"attributes={File.GetAttributes(path)}; {JournalFileHolderProbe.Capture(ownedRoot, path)}"); }
            catch (Exception diagnosticError) { output.WriteLine($"Diagnostic unavailable: {diagnosticError.GetType().Name}: {diagnosticError.Message}"); }
        }
    }

    private static AgentRequest Request(DateTimeOffset? timestamp = null) => new(Guid.NewGuid().ToString("N"), "settings-patch",
        OperationId: Guid.CreateVersion7(timestamp ?? DateTimeOffset.UtcNow).ToString("N"));
    private sealed class TestTime : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class OwnedRoot : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-journal-publication-").FullName;
        internal ProjectPaths Paths => new(Root);
        private string DirectoryPath => Path.Combine(Root, ".autorun-state", CurrentUserIdentity.Key, "operations");
        internal string MetadataPath => Path.Combine(DirectoryPath, "metadata.json");
        internal string ReceiptPath(string id) => Path.Combine(DirectoryPath, id + ".operation.json");
        internal OwnedRoot() => File.WriteAllText(Paths.ConfigPath, "version: 1\ntasks: []\n");
        internal void AssertNoCandidates() => Assert.Empty(Directory.EnumerateFiles(DirectoryPath, "*.tmp"));
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(full).StartsWith("SystemAutorun-journal-publication-", StringComparison.Ordinal)
                || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidOperationException("Refusing unexpected journal test cleanup target"); }
            Directory.Delete(full, true);
        }
    }
}
