using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Common Startup", false)]
    [InlineData("Startup", true)] [InlineData("Common Startup", true)]
    public async Task HistoryOperationOwnerIssuesFromLiveSnapshotAndItsActualIntentTransaction(string source, bool complete)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager, source);
        var original = File.ReadAllBytes(entry.Origin); var request = await HistoryOperationRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths);
        StartupHistoryOperationReference? reference = null; StartupHistoryPayload? candidate = null; string? linkPath = null; string? archivedPath = null; byte[]? issued = null; var calls = 0;
        var response = await RunHistoryOwnerContext(journal, request, async (context, token) =>
        {
            calls++; await using var owner = await manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token);
            candidate = owner.Candidate; reference = Assert.IsType<StartupHistoryOperationReference>(owner.OperationReference);
            Assert.Equal(context.OperationId, reference.OperationId); Assert.Equal(context.Fingerprint, reference.Fingerprint); Assert.Equal(owner.TransactionId, reference.TransactionId);
            var intent = StartupHistoryIntentFormat.Read(File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
            Assert.Equal(owner.TransactionId, intent.TransactionId); Assert.Equal(candidate, intent.Candidate); Assert.NotEqual(context.OperationId, candidate.Binding.RecordId); archivedPath = intent.Record.BackupPath;
            linkPath = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(candidate.Binding.RecordId)); issued = File.ReadAllBytes(linkPath);
            var link = StartupHistoryProtection.UnprotectOperationLink(issued); Assert.Equal(reference, link.Reference); Assert.Equal(candidate, link.Candidate);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(store.DirectoryPath, StartupHistoryStoreReader.ProofName(candidate.Binding.RecordId, "candidate"))))), link.CandidateProtectedDigest);
            Assert.Equal("associated_candidate", OperationOwnerReader(store).Inspect(candidate.Binding.Context, reference).State);
            Assert.Equal(entry.Source, candidate.Binding.Source.Source); Assert.Equal(entry.Name, candidate.Binding.Name); Assert.True(File.Exists(entry.Origin));
            Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
            Assert.Equal(new[] { StartupMutationOperation.IntentPublication, StartupMutationOperation.TemporaryCleanup }, store.History.Owned.Operations.Calls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishOperationLinkAsync(token, null));
            if (complete)
            {
                var native = await owner.ArchiveAndPublishLedgerAsync(token); Assert.True(native.NativeWritePerformed); Assert.True(native.LedgerVerified);
                Assert.True((await owner.PublishCommittedHistoryAsync(token)).Committed); Assert.True((await owner.FinalizeCommittedAsync(token)).TransactionFinalized);
                Assert.Equal("associated_committed", OperationOwnerReader(store).Inspect(candidate.Binding.Context, reference).State);
                Assert.False(File.Exists(entry.Origin)); Assert.True(File.Exists(intent.Record.BackupPath));
            }
            Assert.Equal(issued, File.ReadAllBytes(linkPath)); Assert.Equal(0, store.History.Owned.Native.WritableOpens);
            // Synthetic callback result only: no production receipt adapter is installed.
            return new AgentResponse(context.Request.Id, true, new { SyntheticPreparedOnly = !complete });
        });
        Assert.True(response.Ok, response.Error); Assert.NotNull(reference); Assert.NotNull(candidate); Assert.Equal(issued, File.ReadAllBytes(linkPath!));
        // The native owner reserves DELETE; ordinary File.ReadAllBytes cannot share
        // that handle. Compare exact bytes only after the callback has disposed it.
        Assert.Equal(original, File.ReadAllBytes(complete ? archivedPath! : entry.Origin));
        var receipt = await journal.GetAsync(reference.OperationId); Assert.NotNull(receipt); Assert.Equal(reference.Fingerprint, receipt.Fingerprint);
        Assert.Equal(request.Name, receipt.Name); var replay = await RunHistoryOwnerContext(journal, request, (_, _) => { calls++; throw new IOException("Must not issue another owner."); });
        Assert.True(replay.Replayed); Assert.Equal(1, calls); Assert.Equal(issued, File.ReadAllBytes(linkPath!));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "stage-*.tmp"));
        await using var released = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(200));
    }

    [Theory]
    [InlineData("command")] [InlineData("name")] [InlineData("payload")] [InlineData("revision")]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("casing")] [InlineData("null-field")]
    [InlineData("disabled")] [InlineData("registry")] [InlineData("digest")]
    public async Task HistoryOperationOwnerRejectsOtherOrMalformedFrozenRequestsBeforePublishing(string mode)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager); var request = await HistoryOperationRequest(store, entry);
        if (mode == "command") { request = request with { Command = "startup-restore" }; }
        else if (mode == "name") { request = request with { Name = null }; }
        else if (mode == "payload") { request = request with { Payload = null }; }
        else if (mode == "revision") { request = request with { ExpectedRevision = "not-a-revision" }; }
        else
        {
            var selection = StartupInspectionRequest.FromEntry(entry);
            if (mode is "disabled" or "registry" or "digest")
            {
                selection = mode == "disabled" ? selection with { IsDisabled = true } : mode == "registry" ? selection with { Source = "HKCU" } : selection with { MetadataDigest = new string('0', 64) };
                request = request with { Payload = JsonSerializer.SerializeToElement(selection, AgentProtocol.JsonOptions) };
            }
            else
            {
                var json = request.Payload!.Value.GetRawText(); json = mode switch
                {
                    "unknown" => "{\"unexpected\":true," + json[1..], "duplicate" => "{\"source\":\"Startup\"," + json[1..],
                    "casing" => json.Replace("\"source\"", "\"Source\"", StringComparison.Ordinal),
                    _ => json.Replace("\"source\":\"Startup\"", "\"source\":null", StringComparison.Ordinal),
                };
                request = request with { Payload = JsonSerializer.Deserialize<JsonElement>(json) };
            }
        }
        var journal = new OperationJournal(store.History.Owned.Paths); var reached = false;
        await RunHistoryOwnerContext(journal, request, async (context, token) =>
        {
            reached = true; var before = DraftDataImages(store);
            var error = await Record.ExceptionAsync(() => manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token));
            Assert.NotNull(error); Assert.True(error is ArgumentException or JsonException or InvalidOperationException);
            AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
            return new(context.Request.Id, false, ErrorCode: "fixture_rejected");
        }); Assert.True(reached);
    }

    [Theory]
    [InlineData("draft_verified", false, false)] [InlineData("intent_verified", true, false)]
    [InlineData("operation_link_flushed", true, false)] [InlineData("operation_link_published", true, true)] [InlineData("operation_link_verified", true, true)]
    public async Task HistoryOperationOwnerCancellationRetainsOnlyActuallyPublishedEvidence(string boundary, bool pending, bool linked)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryOperationRequest(store, entry); var original = File.ReadAllBytes(entry.Origin); var journal = new OperationJournal(store.History.Owned.Paths);
        await RunHistoryOwnerContext(journal, request, async (context, _) =>
        {
            using var canceled = new CancellationTokenSource(); var reached = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, canceled.Token,
                step => { if (step == boundary) { reached = true; canceled.Cancel(); } })); Assert.True(reached);
            Assert.Equal(pending, File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
            Assert.Equal(linked ? 1 : 0, Directory.Exists(store.DirectoryPath) ? Directory.GetFiles(store.DirectoryPath, "*.operation-v1.dpapi").Length : 0);
            Assert.Equal(original, File.ReadAllBytes(entry.Origin)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
            if (Directory.Exists(store.DirectoryPath)) { Assert.Empty(Directory.GetFiles(store.DirectoryPath, "stage-*.tmp")); }
            await using var released = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(200));
            Assert.Equal(0, store.History.Owned.Native.WritableOpens); return new(context.Request.Id, false, ErrorCode: "fixture_cancelled");
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryOperationOwnerRefusesEveryExistingLinkWithoutAdoptingOrOverwriting(bool matching)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var original = File.ReadAllBytes(entry.Origin); var request = await HistoryOperationRequest(store, entry); byte[]? occupied = null; string? leaf = null;
        await RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), request, async (context, token) =>
        {
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token, step =>
            {
                if (step != "operation_link_flushed") { return; }
                var intent = StartupHistoryIntentFormat.Read(File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
                leaf = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(intent.Candidate.Binding.RecordId));
                var proof = File.ReadAllBytes(Path.Combine(store.DirectoryPath, StartupHistoryStoreReader.ProofName(intent.Candidate.Binding.RecordId, "candidate")));
                occupied = matching ? StartupHistoryProtection.ProtectOperationLink(new(1, context.OperationId, context.Fingerprint, intent.TransactionId,
                    Convert.ToHexStringLower(SHA256.HashData(proof)), intent.Candidate)) : [1, 2, 3]; store.WritePrivateFile(leaf, occupied);
            }));
            Assert.NotNull(occupied); Assert.Equal(occupied, File.ReadAllBytes(leaf!)); Assert.Empty(Directory.GetFiles(store.DirectoryPath, "stage-*.tmp"));
            Assert.Equal(original, File.ReadAllBytes(entry.Origin)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
            return new(context.Request.Id, false, ErrorCode: "fixture_occupied");
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryOperationOwnerReadbackOrLateAclFailureDoesNotReturnOwnerOrRepairLink(bool lateAcl)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryOperationRequest(store, entry); var original = File.ReadAllBytes(entry.Origin); string? path = null; byte[]? security = null; byte[]? changed = null;
        try
        {
            await RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), request, async (context, token) =>
            {
                var error = await Record.ExceptionAsync(() => manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token, step =>
                {
                    if (step != (lateAcl ? "operation_link_verified" : "operation_link_published")) { return; }
                    var intent = StartupHistoryIntentFormat.Read(File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
                    path = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(intent.Candidate.Binding.RecordId));
                    if (lateAcl)
                    {
                        var info = new FileInfo(path); var acl = info.GetAccessControl();
                        // Normalize Windows' first-DACL-write AUTO_INHERITED control
                        // metadata before recording the descriptor we must restore.
                        acl.SetSecurityDescriptorBinaryForm(acl.GetSecurityDescriptorBinaryForm()); info.SetAccessControl(acl);
                        acl = info.GetAccessControl(); security = acl.GetSecurityDescriptorBinaryForm();
                        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Allow)); info.SetAccessControl(acl);
                    }
                    else { changed = File.ReadAllBytes(path); changed[^1] ^= 1; store.WritePrivateFile(path, changed); }
                }));
                Assert.NotNull(error); Assert.NotNull(path); Assert.Equal(original, File.ReadAllBytes(entry.Origin)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
                Assert.Empty(Directory.GetFiles(store.DirectoryPath, "stage-*.tmp")); if (!lateAcl) { Assert.Equal(changed, File.ReadAllBytes(path)); }
                return new(context.Request.Id, false, ErrorCode: "fixture_unverified");
            });
        }
        finally
        {
            if (security is not null)
            { var info = new FileInfo(path!); var acl = info.GetAccessControl(); acl.SetSecurityDescriptorBinaryForm(security); info.SetAccessControl(acl); Assert.Equal(security, info.GetAccessControl().GetSecurityDescriptorBinaryForm()); }
        }
    }

    [Theory]
    [InlineData("missing")] [InlineData("changed")]
    public async Task HistoryOperationOwnerRechecksItsLinkBeforeAnyNativeDispatch(string damage)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager); var original = File.ReadAllBytes(entry.Origin);
        await RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), await HistoryOperationRequest(store, entry), async (context, token) =>
        {
            await using var owner = await manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token);
            var path = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(owner.Candidate.Binding.RecordId));
            if (damage == "missing") { File.Delete(path); } else { var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; store.WritePrivateFile(path, bytes); }
            var result = await owner.ArchiveAndPublishLedgerAsync(token); Assert.Equal("rejected", result.State); Assert.False(result.NativeWriteAttempted); Assert.False(result.NativeWritePerformed);
            await owner.DisposeAsync(); Assert.Equal(original, File.ReadAllBytes(entry.Origin)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
            return new(context.Request.Id, false, ErrorCode: "fixture_link_changed");
        });
    }

    [Fact]
    public async Task HistoryOperationOwnerDoesNotBackfillARawOwnerWithJournalMetadata()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var draft = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), await ConfigurationRevision.ReadAsync(store.History.Owned.Paths));
        await using var raw = await manager.PrepareHistoryDisableAsync(draft, () => store.DirectoryPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => raw.PublishOperationLinkAsync(default, null)); Assert.Null(raw.OperationReference);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.operation-v1.dpapi")); Assert.True(File.Exists(entry.Origin));
    }

    [Fact]
    public async Task HistoryOperationOwnerRejectsEndedOrForeignContextBeforeCreatingAnyHistory()
    {
        using var store = HistoryTransactionStore(); using var foreign = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryOperationRequest(store, entry); OperationExecutionContext? ended = null;
        await RunHistoryOwnerContext(new OperationJournal(foreign.History.Owned.Paths), request, async (context, token) =>
        {
            ended = context; await Assert.ThrowsAsync<ArgumentException>(() => manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token));
            return new(context.Request.Id, false, ErrorCode: "fixture_foreign");
        });
        Assert.NotNull(ended); Assert.False(ended.IsActive); var before = DraftDataImages(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareHistoryDisableAsync(ended, () => store.DirectoryPath));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
    }

    [Fact]
    public async Task HistoryOperationOwnerEscapingItsCallbackCannotDispatchNativeAfterContextEnds()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager); var original = File.ReadAllBytes(entry.Origin);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var leave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupManager.HistoryDisableTransaction? owner = null; OperationExecutionContext? captured = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var workflow = RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), await HistoryOperationRequest(store, entry), async (context, token) =>
        {
            captured = context; owner = await manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token); ready.TrySetResult(); await leave.Task.WaitAsync(token);
            return new(context.Request.Id, true, new { SyntheticPreparedOnly = true });
        }, deadline.Token);
        try
        {
            await ready.Task.WaitAsync(deadline.Token); leave.TrySetResult(); Assert.True(SpinWait.SpinUntil(() => captured is { IsActive: false }, TimeSpan.FromSeconds(5)));
            Assert.NotNull(owner); var result = await owner.ArchiveAndPublishLedgerAsync(); Assert.Equal("rejected", result.State); Assert.False(result.NativeWriteAttempted);
            Assert.True(File.Exists(entry.Origin)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
        }
        finally { leave.TrySetResult(); if (owner is not null) { await owner.DisposeAsync(); } await workflow; }
        Assert.Equal(original, File.ReadAllBytes(entry.Origin));
    }

    [Fact]
    public async Task HistoryOperationOwnerEscapedPreparationCannotPublishAfterCapturedDraftContextEnds()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); IDisposable? admission = null;
        Task<StartupManager.HistoryDisableTransaction>? pending = null; OperationExecutionContext? captured = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), await HistoryOperationRequest(store, entry), async (context, token) =>
            {
                captured = context;
                // Deliberate bad-caller escape: R49 is finished. Core either waits
                // for held admission or rejects earlier; neither may publish later.
                pending = manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token, step =>
                {
                    if (step != "operation_draft_captured") { return; }
                    admission = StartupMutationLease.AcquireAsync(store.History.Owned.Paths, token).GetAwaiter().GetResult(); ready.TrySetResult();
                });
                await ready.Task.WaitAsync(token); return new(context.Request.Id, true, new { SyntheticEscapedPreparation = true });
            }, deadline.Token);
            Assert.NotNull(captured); Assert.False(captured.IsActive); Assert.NotNull(pending);
            admission!.Dispose(); admission = null;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => { await pending; });
            Assert.False(Directory.Exists(store.DirectoryPath)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
            Assert.True(File.Exists(entry.Origin)); Assert.Empty(store.History.Owned.Operations.Calls);
        }
        finally
        {
            admission?.Dispose();
            if (pending is not null) { var ignoredExpectedFailure = await Record.ExceptionAsync(async () => { await pending; }); Assert.IsType<InvalidOperationException>(ignoredExpectedFailure); }
        }
    }

    [Fact]
    public async Task HistoryOperationOwnerCannotIssueAgainAfterNativeBeforeLedgerWhileHistoryStillCandidate()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        await RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), await HistoryOperationRequest(store, entry), async (context, token) =>
        {
            await using var owner = await manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token);
            var path = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(owner.Candidate.Binding.RecordId)); var before = File.ReadAllBytes(path);
            using var canceled = new CancellationTokenSource();
            var result = await owner.ArchiveAndPublishLedgerAsync(canceled.Token, step => { if (step == "native_renamed") { canceled.Cancel(); } });
            Assert.True(result.NativeWriteAttempted); Assert.True(result.NativeWritePerformed); Assert.False(result.LedgerVerified);
            Assert.Equal("associated_candidate", OperationOwnerReader(store).Inspect(owner.Candidate.Binding.Context, owner.OperationReference!).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishOperationLinkAsync(token, null)); Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.operation-v1.dpapi")); return new(context.Request.Id, false, ErrorCode: "fixture_uncommitted_native");
        });
    }

    [Theory]
    [InlineData("candidate")] [InlineData("record")] [InlineData("manager")]
    public async Task HistoryOperationOwnerOpaqueDraftRejectsRawRebindingAndHasNoContextConstructorBridge(string changed)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); using var otherManager = store.History.Owned.Manager();
        var entry = await DraftEntry(store, manager); StartupManager.HistoryOperationDraft? captured = null;
        await RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), await HistoryOperationRequest(store, entry), async (context, token) =>
        {
            captured = await StartupManager.HistoryOperationDraft.CaptureAsync(manager, context, token, null); var draft = captured.Draft;
            captured.Require(manager, draft.Candidate, draft.Record);
            Assert.Throws<InvalidOperationException>(() => captured.Require(changed == "manager" ? otherManager : manager,
                changed == "candidate" ? draft.Candidate with { Nonce = Guid.NewGuid().ToString("N") } : draft.Candidate,
                changed == "record" ? draft.Record with { Arguments = "different-selector-metadata" } : draft.Record));
            var constructors = typeof(StartupManager.HistoryOperationDraft).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.True(Assert.Single(constructors).IsPrivate);
            Assert.DoesNotContain(typeof(StartupManager.HistoryDisableTransaction).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(OperationExecutionContext)));
            Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls); return new(context.Request.Id, true, new { SyntheticDraftOnly = true });
        });
        Assert.NotNull(captured); Assert.Throws<InvalidOperationException>(() => captured.Require(manager, captured.Draft.Candidate, captured.Draft.Record));
    }

    [Fact]
    public async Task HistoryOperationOwnerTestHarnessRethrowsCallbackFaultInsteadOfPassingAnUnknownReceipt()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryOperationRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths);
        var error = await Assert.ThrowsAsync<IOException>(() => RunHistoryOwnerContext(journal, request, (_, _) => throw new IOException("owned-test-sentinel")));
        Assert.Equal("owned-test-sentinel", error.Message); Assert.Equal(OperationState.Unknown, (await journal.GetAsync(request.OperationId!))!.State);
        Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
    }

    private static async Task<AgentResponse> RunHistoryOwnerContext(OperationJournal journal, AgentRequest request,
        Func<OperationExecutionContext, CancellationToken, Task<AgentResponse>> callback, CancellationToken token = default)
    {
        Exception? failure = null;
        var response = await journal.RunWithContextAsync(request, async (context, cancellation) =>
        {
            try { return await callback(context, cancellation); }
            catch (Exception error) { failure = error; throw; }
        }, token);
        // Journal intentionally converts callback faults into unknown receipts. A test
        // must not let that conversion swallow an assertion or unexpected IO failure.
        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        return response;
    }

    private static async Task<AgentRequest> HistoryOperationRequest(HistoryStoreFixture store, StartupEntry entry) => new(
        Guid.NewGuid().ToString("N"), "startup-disable", entry.Id, JsonSerializer.SerializeToElement(StartupInspectionRequest.FromEntry(entry), AgentProtocol.JsonOptions),
        ExpectedRevision: await ConfigurationRevision.ReadAsync(store.History.Owned.Paths), OperationId: AgentProtocol.NewOperationId());
    private static StartupHistoryOperationStoreReader OperationOwnerReader(HistoryStoreFixture store) => new(store.History.Owned.Root, () => store.DirectoryPath);
}
