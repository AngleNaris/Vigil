using System.Diagnostics;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Tests;

public sealed class RecoveryControlStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Identity = new('a', 64);

    [Fact]
    public async Task PauseSurvivesReopenAndRelocationButRevisionIsRootScoped()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var initial = await store.ReadAsync();
        var paused = await store.SetPausedAsync("task", true, initial.Revision);
        var entry = Assert.Single((await fixture.Open().ReadAsync()).Tasks);
        Assert.True(entry.Paused);
        Assert.Equal(Start, entry.PausedAt);
        Assert.Null(entry.AttemptId); // Persisted stop intent is NOT proof the workload stopped.
        fixture.MoveWorkspace();
        var moved = fixture.Open();
        var movedState = await moved.ReadAsync();
        Assert.True(Assert.Single(movedState.Tasks).Paused);
        Assert.NotEqual(paused.Revision, movedState.Revision);
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() => moved.SetPausedAsync("task", false, paused.Revision));
        Assert.Equal("recovery_revision_conflict", conflict.Code);
    }

    [Fact]
    public async Task AutomaticAdmissionIsAtLeastTwoMinutesApartAndCaseInsensitive()
    {
        using var fixture = new Fixture();
        var first = await fixture.Open().TryAdmitAutomaticAsync("task", Identity);
        Assert.NotNull(first.Attempt);
        await fixture.Open().CompleteAsync("task", first.Attempt.AttemptId!, RecoveryAttemptOutcome.Failed);
        Assert.Null((await fixture.Open().TryAdmitAutomaticAsync("TASK", Identity)).Attempt);
        fixture.Clock.Now = Start.AddSeconds(119.999);
        Assert.Null((await fixture.Open().TryAdmitAutomaticAsync("task", Identity)).Attempt);
        fixture.Clock.Now = Start.AddMinutes(2);
        var second = await fixture.Open().TryAdmitAutomaticAsync("TASK", Identity);
        Assert.NotNull(second.Attempt);
        Assert.NotEqual(first.Attempt.AttemptId, second.Attempt.AttemptId);
        Assert.Single(second.State.Tasks);
    }

    [Fact]
    public async Task TargetChangeDoesNotBypassNameScopedCooldown()
    {
        using var fixture = new Fixture();
        var attempt = await fixture.Open().TryAdmitAutomaticAsync("task", Identity);
        await fixture.Open().CompleteAsync("task", attempt.Attempt!.AttemptId!, RecoveryAttemptOutcome.Failed);
        fixture.Clock.Now = Start.AddSeconds(1);
        Assert.Null((await fixture.Open().TryAdmitAutomaticAsync("task", new string('b', 64))).Attempt);
        Assert.NotNull((await fixture.Open().BeginManualStartAsync("task", new string('b', 64))).Attempt);
    }

    [Fact]
    public async Task BackwardClockDoesNotClearCooldown()
    {
        using var fixture = new Fixture();
        var attempt = await fixture.Open().TryAdmitAutomaticAsync("task", Identity);
        await fixture.Open().CompleteAsync("task", attempt.Attempt!.AttemptId!, RecoveryAttemptOutcome.Failed);
        fixture.Clock.Now = Start.AddDays(-1);
        Assert.Null((await fixture.Open().TryAdmitAutomaticAsync("task", Identity)).Attempt);
    }

    [Fact]
    public async Task CompletionCannotOverwriteNewerStopIntentOrChangeCommittedOutcome()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var admission = await store.TryAdmitAutomaticAsync("task", Identity);
        await store.SetPausedAsync("task", true);
        var done = await store.CompleteAsync("task", admission.Attempt!.AttemptId!, RecoveryAttemptOutcome.Succeeded);
        var entry = Assert.Single(done.Tasks);
        Assert.True(entry.Paused);
        Assert.Equal(RecoveryAttemptOutcome.Succeeded, entry.Outcome);
        var duplicate = await store.CompleteAsync("TASK", entry.AttemptId!, RecoveryAttemptOutcome.Failed);
        Assert.Equal(done.Revision, duplicate.Revision);
    }

    [Fact]
    public async Task ManualStartClearsPauseAndReservesAttemptAtomicallyWithoutErasingCooldown()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        await store.SetPausedAsync("task", true);
        Assert.Null((await store.TryAdmitAutomaticAsync("task", Identity)).Attempt);
        var manual = await store.BeginManualStartAsync("task", Identity);
        Assert.False(manual.Attempt!.Paused);
        Assert.Null(manual.Attempt.PausedAt);
        Assert.Equal(Start, manual.Attempt.LastAttemptAt);
        Assert.Equal(RecoveryAttemptOutcome.Admitted, manual.Attempt.Outcome);
        Assert.Null((await store.TryAdmitAutomaticAsync("task", Identity)).Attempt);
        await store.SetPausedAsync("task", true);
        await store.SetPausedAsync("task", false);
        Assert.Null((await store.TryAdmitAutomaticAsync("task", Identity)).Attempt);
    }

    [Fact]
    public async Task SupersededCompletionDoesNotTouchTheNewAttempt()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var old = await store.BeginManualStartAsync("task", Identity);
        var current = await store.BeginManualStartAsync("task", Identity);
        var ignored = await store.CompleteAsync("task", old.Attempt!.AttemptId!, RecoveryAttemptOutcome.Succeeded);
        Assert.Equal(current.State.Revision, ignored.Revision);
        Assert.Equal(RecoveryAttemptOutcome.Admitted, Assert.Single(ignored.Tasks).Outcome);
    }

    [Fact]
    public async Task InterruptedAdmissionBecomesUnknownWithoutReplayOrPauseReset()
    {
        using var fixture = new Fixture();
        var original = await fixture.Open().TryAdmitAutomaticAsync("task", Identity);
        await fixture.Open().SetPausedAsync("task", true);
        var restarted = fixture.Open();
        var repaired = await restarted.MarkInterruptedAttemptsUnknownAsync();
        var entry = Assert.Single(repaired.Tasks);
        Assert.Equal(original.Attempt!.AttemptId, entry.AttemptId);
        Assert.Equal(Start, entry.LastAttemptAt);
        Assert.Equal(RecoveryAttemptOutcome.OutcomeUnknown, entry.Outcome);
        Assert.True(entry.Paused);
        Assert.Equal(repaired.Revision, (await restarted.MarkInterruptedAttemptsUnknownAsync()).Revision);
        await restarted.SetPausedAsync("task", false);
        Assert.Null((await restarted.TryAdmitAutomaticAsync("task", Identity)).Attempt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"tasks\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"tasks\":null}")]
    public async Task CorruptStateIsNotResetOrOverwritten(string raw)
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, raw);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetPausedAsync("task", true));
        Assert.Equal(raw, await File.ReadAllTextAsync(store.FilePath));
    }

    [Fact]
    public async Task CapacityNeverEvictsAnExistingPauseAndOversizeInputIsRejected()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        var entries = Enumerable.Range(0, RecoveryControlStore.MaximumEntries)
            .Select(index => new RecoveryTaskState($"task-{index}", true, Start)).ToArray();
        var raw = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, tasks = entries }, AgentProtocol.JsonOptions);
        await File.WriteAllBytesAsync(store.FilePath, raw);
        Assert.Equal(entries.Length, (await store.ReadAsync()).Tasks.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetPausedAsync("extra", true));
        Assert.Equal(raw, await File.ReadAllBytesAsync(store.FilePath));
        await File.WriteAllBytesAsync(store.FilePath, new byte[RecoveryControlStore.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task IndependentStoreWritersDoNotLoseIntentAndStaleCasDoesNotWrite()
    {
        using var fixture = new Fixture();
        var before = await fixture.Open().ReadAsync();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => fixture.Open().SetPausedAsync($"task-{index}", true)));
        var after = await fixture.Open().ReadAsync();
        Assert.Equal(16, after.Tasks.Count);
        Assert.All(after.Tasks, entry => Assert.True(entry.Paused));
        await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Open().SetPausedAsync("task-0", false, before.Revision));
        Assert.Equal(after.Revision, (await fixture.Open().ReadAsync()).Revision);
    }

    [Fact]
    public async Task DifferentRootsCannotReuseEachOthersRecoveryRevision()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var revision = (await first.Open().ReadAsync()).Revision;
        Assert.NotEqual(revision, (await second.Open().ReadAsync()).Revision);
        await Assert.ThrowsAsync<AgentCommandException>(() => second.Open().SetPausedAsync("task", true, revision));
        Assert.Empty((await second.Open().ReadAsync()).Tasks);
    }

    [Fact]
    public async Task UnfinishedAdmissionCannotBeOverwrittenAfterCooldownOrFailedOutcomeWrite()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var admission = await store.TryAdmitAutomaticAsync("task", Identity);
        using (var blocked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => store.CompleteAsync("task", admission.Attempt!.AttemptId!, RecoveryAttemptOutcome.Failed));
        }
        fixture.Clock.Now = Start.AddMinutes(3);
        var next = await fixture.Open().TryAdmitAutomaticAsync("task", Identity);
        Assert.Null(next.Attempt);
        Assert.Equal(admission.Attempt!.AttemptId, Assert.Single(next.State.Tasks).AttemptId);
        Assert.Equal(RecoveryAttemptOutcome.Admitted, Assert.Single(next.State.Tasks).Outcome);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task CompletionNormalizesAnOtherwiseValidUuidBeforeMatching()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var admission = await store.TryAdmitAutomaticAsync("task", Identity);
        var done = await store.CompleteAsync("task", admission.Attempt!.AttemptId!.ToUpperInvariant(), RecoveryAttemptOutcome.Succeeded);
        Assert.Equal(RecoveryAttemptOutcome.Succeeded, Assert.Single(done.Tasks).Outcome);
    }

    [Fact]
    public async Task MissingStateCannotAuthorizeRecoveryOrSilentlyRecreateOldIntent()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        await store.SetPausedAsync("task", true);
        var exact = Path.GetFullPath(store.FilePath);
        Assert.Equal(Path.Combine(fixture.Root, ".autorun-state", CurrentUserIdentity.Key, "recovery-v1.json"), exact);
        File.Delete(exact);
        var missing = await fixture.Open().ReadAsync();
        Assert.False(missing.Initialized);
        Assert.Null((await fixture.Open().TryAdmitAutomaticAsync("task", Identity)).Attempt);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetPausedAsync("task", false));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.BeginManualStartAsync("task", Identity));
        Assert.False(File.Exists(exact));
        var reset = await store.InitializeEmptyAsync(missing.Revision); // Explicit operator acknowledgement, never startup fallback.
        Assert.True(reset.Initialized);
        Assert.True((await fixture.Open().ReadAsync()).Initialized);
        Assert.Equal(reset.Revision, (await store.InitializeEmptyAsync(reset.Revision)).Revision);
    }

    [Fact]
    public async Task UncommittedTemporaryIntentFailsClosedAndIsNotDeletedByAPrefixRule()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var temporary = Path.Combine(Path.GetDirectoryName(store.FilePath)!, ".recovery-0123456789abcdef0123456789abcdef.tmp");
        await File.WriteAllTextAsync(temporary, "owned sentinel representing an interrupted intent");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TryAdmitAutomaticAsync("task", Identity));
        Assert.Equal("owned sentinel representing an interrupted intent", await File.ReadAllTextAsync(temporary));
    }

    [Fact]
    public async Task AColdRootNeedsExplicitInitializationBeforeAutomaticAdmission()
    {
        using var fixture = new Fixture(initializeState: false);
        var store = fixture.Open();
        var missing = await store.ReadAsync();
        Assert.False(missing.Initialized);
        Assert.Null((await store.TryAdmitAutomaticAsync("task", Identity)).Attempt);
        Assert.False(Directory.Exists(Path.GetDirectoryName(store.FilePath)));
        var initialized = await store.InitializeEmptyAsync(missing.Revision);
        Assert.True(initialized.Initialized);
        Assert.NotNull((await store.TryAdmitAutomaticAsync("task", Identity)).Attempt);
    }

    [Fact]
    public async Task ResumingAnUnknownNameDoesNotConsumeCapacity()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var before = await store.ReadAsync();
        var after = await store.SetPausedAsync("never-known", false);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Empty(after.Tasks);
    }

    [Fact]
    public async Task TypedHeldLeaseAllowsAtomicPolicyAndIntentWithoutReentry()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "autorun.yaml"), "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
        var paths = new ProjectPaths(fixture.Root);
        var workspace = new ConfigurationWorkspace(paths);
        var store = fixture.Open();
        await using var lease = await RootWriteLease.AcquireAsync(paths);
        var configuration = await workspace.ReadWithLeaseAsync(lease).WaitAsync(TimeSpan.FromSeconds(3));
        var state = await store.ReadAsync(heldLease: lease).WaitAsync(TimeSpan.FromSeconds(3));
        var paused = await store.SetPausedAsync("task", true, state.Revision, heldLease: lease, recordIntent: true).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(Assert.Single(paused.Tasks).IntentId);
        Assert.Equal(configuration.Revision, (await workspace.ReadWithLeaseAsync(lease)).Revision);
    }

    [Fact]
    public async Task WrongRootOrDisposedHeldLeaseIsRejected()
    {
        using var first = new Fixture(); using var second = new Fixture();
        var lease = await RootWriteLease.AcquireAsync(new ProjectPaths(first.Root));
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => second.Open().ReadAsync(heldLease: lease));
            await Assert.ThrowsAsync<ArgumentException>(() => new ConfigurationWorkspace(new ProjectPaths(second.Root)).ReadWithLeaseAsync(lease));
        }
        finally { await lease.DisposeAsync(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Open().ReadAsync(heldLease: lease));
    }

    [Fact]
    public async Task EveryExplicitStopHasANewIntentTokenAndStartSupersedesIt()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        var first = await store.SetPausedAsync("task", true, recordIntent: true);
        var second = await store.SetPausedAsync("task", true, recordIntent: true);
        Assert.NotEqual(Assert.Single(first.Tasks).IntentId, Assert.Single(second.Tasks).IntentId);
        var start = await store.BeginManualStartAsync("task", Identity);
        Assert.Equal(start.Attempt!.AttemptId, start.Attempt.IntentId);
        Assert.NotEqual(Assert.Single(second.Tasks).IntentId, start.Attempt.IntentId);
    }

    [Fact]
    public async Task MalformedStoredAttemptIsAStateErrorNotAClientOperationIdError()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var bad = new RecoveryTaskState("task", AttemptId: "bad-id", TaskIdentity: Identity,
            LastAttemptAt: Start, Outcome: RecoveryAttemptOutcome.Admitted);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { schemaVersion = 1, tasks = new[] { bad } }, AgentProtocol.JsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync());
    }

    [Fact]
    public async Task StateDirectoryJunctionIsRejectedWithoutTouchingItsTarget()
    {
        using var fixture = new Fixture(initializeState: false);
        var foreign = Path.Combine(fixture.Base, "foreign");
        Directory.CreateDirectory(foreign);
        var sentinel = Path.Combine(foreign, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "must remain unchanged");
        var alias = Path.Combine(fixture.Root, ".autorun-state");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, foreign }) { start.ArgumentList.Add(argument); }
        using var command = Process.Start(start)!;
        try
        {
            await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, command.ExitCode);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open().ReadAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open().SetPausedAsync("task", true));
            Assert.Equal("must remain unchanged", await File.ReadAllTextAsync(sentinel));
            Assert.Single(Directory.GetFileSystemEntries(foreign));
        }
        finally
        {
            if (!command.HasExited) { command.Kill(); await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            if (Directory.Exists(alias))
            {
                Assert.Equal(fixture.Root, Path.GetDirectoryName(Path.GetFullPath(alias)));
                Assert.True(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint));
                Directory.Delete(alias);
            }
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public string Base { get; } = Directory.CreateTempSubdirectory("SystemAutorun-recovery-tests-").FullName;
        public string Root { get; private set; }
        public Clock Clock { get; } = new();
        public Fixture(bool initializeState = true)
        {
            Root = Path.Combine(Base, "workspace");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "autorun.yaml"), "version: 1\ntasks: []\n");
            // Fixture setup explicitly seeds a known empty control state, never an automatic reset.
            if (initializeState)
            {
                var store = Open();
                Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
                File.WriteAllText(store.FilePath, "{\"schemaVersion\":1,\"tasks\":[]}");
            }
        }
        public RecoveryControlStore Open() => new(new ProjectPaths(Root), Clock);
        public void MoveWorkspace()
        {
            var target = Path.Combine(Base, "moved");
            Assert.Equal(Base, Path.GetDirectoryName(Path.GetFullPath(Root)));
            Assert.Equal(Base, Path.GetDirectoryName(Path.GetFullPath(target)));
            Directory.Move(Root, target);
            Root = target;
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Base);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Assert.StartsWith("SystemAutorun-recovery-tests-", Path.GetFileName(resolved));
            Directory.Delete(resolved, recursive: true);
        }
    }
}
