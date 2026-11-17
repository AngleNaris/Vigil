using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Startup", true)]
    [InlineData("Common Startup", false)] [InlineData("Common Startup", true)]
    public void HistoryCurrentUserCodecRoundTripsButNeverGrantsNativeAuthority(string source, bool committed)
    {
        using var history = new HistoryFixture(source, committed); var payload = history.Payload;
        var encoded = StartupHistoryFormat.Write(payload); Assert.Equal(payload, StartupHistoryFormat.Read(encoded));
        var cipher = StartupHistoryProtection.Protect(payload); var originalCipher = cipher.ToArray();
        Assert.False(cipher.AsSpan().IndexOf(encoded) >= 0); Assert.Equal(payload, StartupHistoryProtection.Unprotect(cipher));
        var check = StartupHistoryProtection.Inspect(cipher, payload.Binding, HistoryLatest(payload, cipher));
        Assert.Equal(committed ? "protected_committed" : "candidate", check.State); AssertHistoryNoAuthority(check, history);
        Assert.Equal(originalCipher, cipher); Assert.False(File.Exists(history.Owned.Paths.StartupBackupPath));
        CryptographicOperations.ZeroMemory(encoded);
    }

    [Theory]
    [InlineData("version")] [InlineData("phase")] [InlineData("candidate-commit-time")]
    [InlineData("committed-no-time")] [InlineData("commit-before-candidate")] [InlineData("non-utc")]
    [InlineData("nonce")] [InlineData("zero-nonce")] [InlineData("upper-nonce")] [InlineData("sequence")]
    [InlineData("disposition")] [InlineData("null-binding")] [InlineData("null-source")] [InlineData("null-original")]
    [InlineData("actor")] [InlineData("source-kind")] [InlineData("source-owner")] [InlineData("known-folder")]
    [InlineData("root-path")] [InlineData("archive-layout")] [InlineData("case-mode")]
    [InlineData("file-id")] [InlineData("zero-file-id")] [InlineData("volume")] [InlineData("cross-volume")]
    [InlineData("links")] [InlineData("length")] [InlineData("hash")] [InlineData("record-id")]
    [InlineData("record-digest")] [InlineData("name")] [InlineData("missing")] [InlineData("unknown")]
    [InlineData("nested-unknown")] [InlineData("case-alias")]
    public void HistoryStrictFormatRejectsMalformedOrContradictoryBindings(string mode)
    {
        using var history = new HistoryFixture(); var root = JsonNode.Parse(StartupHistoryFormat.Write(history.Payload))!.AsObject();
        var binding = root["binding"]!.AsObject(); var source = binding["source"]!.AsObject();
        var original = binding["original"]!.AsObject(); var directory = binding["root"]!.AsObject();
        switch (mode)
        {
            case "version": root["schemaVersion"] = 2; break;
            case "phase": root["phase"] = "trusted"; break;
            case "candidate-commit-time": root["phase"] = "candidate"; break;
            case "committed-no-time": root["committedAt"] = null; break;
            case "commit-before-candidate": root["committedAt"] = history.Payload.CandidateAt.AddSeconds(-1); break;
            case "non-utc": root["candidateAt"] = history.Payload.CandidateAt.ToOffset(TimeSpan.FromHours(1)); break;
            case "nonce": root["nonce"] = new string('z', 32); break;
            case "zero-nonce": root["nonce"] = new string('0', 32); break;
            case "upper-nonce": root["nonce"] = new string('A', 32); break;
            case "sequence": root["sequence"] = 0; break;
            case "disposition": root["disposition"] = "restored"; break;
            case "null-binding": root["binding"] = null; break;
            case "null-source": binding["source"] = null; break;
            case "null-original": binding["original"] = null; break;
            case "actor": binding["actorSid"] = "not-a-sid"; break;
            case "source-kind": source["kind"] = "machine"; break;
            case "source-owner": source["userSid"] = null; break;
            case "known-folder": source["knownFolder"] = "CommonStartup"; break;
            case "root-path": directory["path"] = @"\\offline.invalid\never-probe"; break;
            case "archive-layout": binding["archive"]!["path"] = history.Owned.Root; break;
            case "case-mode": directory["caseSensitivity"] = "unknown"; break;
            case "file-id": original["identity"]!["fileId"] = new string('A', 32); break;
            case "zero-file-id": original["identity"]!["fileId"] = new string('0', 32); break;
            case "volume": directory["identity"]!["volumeSerialNumber"] = new string('g', 16); break;
            case "cross-volume": original["identity"]!["volumeSerialNumber"] = DifferentHistoryVolume(history.Payload.Binding.Original.Identity.VolumeSerialNumber); break;
            case "links": original["linkCount"] = 2; break;
            case "length": original["length"] = 65537; break;
            case "hash": original["sha256"] = new string('G', 64); break;
            case "record-id": binding["recordId"] = "../escape"; break;
            case "record-digest": binding["recordDigest"] = ""; break;
            case "name": binding["name"] = @"..\escape"; break;
            case "missing": root.Remove("sequence"); break;
            case "unknown": root["nativeWriteReady"] = true; break;
            case "nested-unknown": directory["identity"]!["otherId"] = "fake"; break;
            case "case-alias": root.Remove("schemaVersion"); root["SchemaVersion"] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        Assert.Throws<JsonException>(() => StartupHistoryFormat.Read(Encoding.UTF8.GetBytes(root.ToJsonString())));
        AssertHistoryNoAuthority(new("unverified", "fixture_rejection"), history);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryStrictFormatRejectsDuplicatePropertiesAtEveryLevel(bool nested)
    {
        using var history = new HistoryFixture(); var text = Encoding.UTF8.GetString(StartupHistoryFormat.Write(history.Payload));
        text = nested ? text.Replace("\"caseSensitivity\":\"insensitive\"", "\"caseSensitivity\":\"insensitive\",\"caseSensitivity\":\"insensitive\"", StringComparison.Ordinal)
            : text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => StartupHistoryFormat.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("root")] [InlineData("source")] [InlineData("archive")]
    public void HistorySamePathDirectoryReplacementCannotMatchTheCapturedPhysicalIdentity(string subject)
    {
        using var history = new HistoryFixture(); var binding = history.Payload.Binding;
        var path = subject == "root" ? history.Owned.Root : subject == "source" ? binding.Source.Directory.Path : binding.Archive.Path;
        var saved = path + ".owned-saved";
        Assert.Equal(Path.GetFullPath(saved), saved); Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(saved));
        Assert.False(Directory.Exists(saved)); Assert.False(File.Exists(saved));
        Directory.Move(path, saved);
        try
        {
            Directory.CreateDirectory(path); var observed = HistoryDirectory(history.Owned, path);
            var current = subject switch
            {
                "root" => binding with { Root = observed },
                "source" => binding with { Source = binding.Source with { Directory = observed } },
                _ => binding with { Archive = observed },
            };
            var cipher = StartupHistoryProtection.Protect(history.Payload);
            var result = StartupHistoryProtection.Inspect(cipher, current, HistoryLatest(history.Payload, cipher));
            Assert.Equal("history_context_changed", result.Reason); AssertHistoryNoAuthority(result, history);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(path), path); Assert.Empty(Directory.EnumerateFileSystemEntries(path));
            Directory.Delete(path); Directory.Move(saved, path);
        }
    }

    [Theory]
    [InlineData("latest-missing")] [InlineData("latest-sequence")] [InlineData("latest-nonce")]
    [InlineData("latest-phase")] [InlineData("latest-invalid")] [InlineData("record")] [InlineData("other-record")]
    [InlineData("name")] [InlineData("bytes")] [InlineData("actor")]
    public void HistoryCannotSelfDeclareLatestOrRebindToOtherCurrentFacts(string mode)
    {
        using var history = new HistoryFixture(); var payload = history.Payload;
        var cipher = StartupHistoryProtection.Protect(payload); StartupHistoryLatest? latest = HistoryLatest(payload, cipher); var current = payload.Binding;
        switch (mode)
        {
            case "latest-missing": latest = null; break;
            case "latest-sequence": latest = latest with { Sequence = latest.Sequence + 1 }; break;
            case "latest-nonce": latest = latest with { Nonce = Guid.NewGuid().ToString("N") }; break;
            case "latest-phase": latest = latest with { Phase = "candidate" }; break;
            case "latest-invalid": latest = latest with { Sequence = 0 }; break;
            case "record": current = current with { RecordDigest = new string('a', 64) }; break;
            case "other-record": current = current with { RecordId = Guid.NewGuid().ToString("N") }; break;
            case "name": current = current with { Name = "other" }; break;
            case "bytes": current = current with { Original = current.Original with { Sha256 = new string('a', 64) } }; break;
            case "actor":
                var sid = current.ActorSid == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18";
                current = current with { ActorSid = sid, Source = current.Source with { UserSid = sid } }; break;
        }
        var result = StartupHistoryProtection.Inspect(cipher, current, latest);
        Assert.Equal("unverified", result.State); AssertHistoryNoAuthority(result, history);
    }

    [Theory]
    [InlineData("first")] [InlineData("last")] [InlineData("truncate")] [InlineData("append")]
    public void HistoryAlteredProtectedBytesNeverMatchTheIndependentLatestDigest(string mode)
    {
        using var history = new HistoryFixture(); var cipher = StartupHistoryProtection.Protect(history.Payload);
        var latest = HistoryLatest(history.Payload, cipher); var altered = cipher.ToArray();
        if (mode == "first") { altered[0] ^= 1; }
        else if (mode == "last") { altered[^1] ^= 1; }
        else if (mode == "truncate") { altered = altered[..^1]; }
        else { altered = [.. altered, 0]; }
        var result = StartupHistoryProtection.Inspect(altered, history.Payload.Binding, latest);
        Assert.Equal("history_protected_bytes_changed", result.Reason); AssertHistoryNoAuthority(result, history);
        if (mode != "append")
        {
            var error = Record.Exception(() => StartupHistoryProtection.Unprotect(altered));
            Assert.True(error is CryptographicException or JsonException);
        }
        Assert.Equal(history.Payload, StartupHistoryProtection.Unprotect(cipher));
    }

    [Fact]
    public void HistoryMissingOversizeImpersonatedAndCanceledInputsCannotCreateAuthority()
    {
        using var history = new HistoryFixture(); var payload = history.Payload;
        var cipher = StartupHistoryProtection.Protect(payload); var latest = HistoryLatest(payload, cipher);
        Assert.Equal("history_missing", StartupHistoryProtection.Inspect([], payload.Binding, latest).Reason);
        Assert.Equal("unverified", StartupHistoryProtection.Inspect(new byte[StartupHistoryProtection.MaximumProtectedBytes + 1], payload.Binding, latest).State);
        Assert.Throws<CryptographicException>(() => StartupHistoryProtection.Unprotect(new byte[StartupHistoryProtection.MaximumProtectedBytes + 1]));
        Assert.Throws<JsonException>(() => StartupHistoryFormat.Read(new byte[StartupHistoryFormat.MaximumBytes + 1]));
        using var identity = WindowsIdentity.GetCurrent();
        WindowsIdentity.RunImpersonated(identity.AccessToken, () =>
        {
            Assert.Throws<CryptographicException>(() => StartupHistoryProtection.Protect(payload));
            Assert.Throws<CryptographicException>(() => StartupHistoryProtection.Unprotect(cipher));
            Assert.Equal("history_protection_invalid", StartupHistoryProtection.Inspect(cipher, payload.Binding, latest).Reason);
        });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StartupHistoryProtection.Protect(payload, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => StartupHistoryProtection.Unprotect(cipher, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => StartupHistoryProtection.Inspect(cipher, payload.Binding, latest, canceled.Token));
        Assert.Equal(payload, StartupHistoryProtection.Unprotect(cipher)); AssertHistoryNoAuthority(new("unverified", null), history);
    }

    [Fact]
    public void HistoryRecordAssociationUsesExplicitSemanticFieldsNotLedgerSpellingOrIpcDigest()
    {
        using var history = new HistoryFixture(); var row = history.Record;
        var digest = StartupHistoryFormat.RecordDigest(row);
        Assert.Equal(digest, StartupHistoryFormat.RecordDigest(row with { Disposition = null, DisabledAt = row.DisabledAt.ToOffset(TimeSpan.FromHours(3)) }));
        var rewritten = Assert.Single(StartupBackupLedger.Read(StartupBackupLedger.Write([row]), requireComplete: true).Records);
        Assert.Equal(digest, StartupHistoryFormat.RecordDigest(rewritten));
        Assert.NotEqual(digest, StartupHistoryFormat.RecordDigest(row with { Target = "never-probe.exe" }));
        Assert.NotEqual(digest, StartupHistoryFormat.RecordDigest(row with { Name = "other" }));
        Assert.NotEqual(digest, StartupHistoryFormat.RecordDigest(row with { Arguments = "changed" }));
        Assert.NotEqual(digest, StartupHistoryFormat.RecordDigest(row with { Disposition = "restored", RestoredAt = DateTimeOffset.UtcNow }));
        AssertHistoryNoAuthority(new("unverified", null), history);
    }

    [Fact]
    public void HistoryByteLimitsAreEnforcedForDecodedAndEncodedPayloadsWithoutPathProbes()
    {
        using var history = new HistoryFixture(); var payload = history.Payload;
        var encoded = StartupHistoryFormat.Write(payload); var exact = new byte[StartupHistoryFormat.MaximumBytes];
        Array.Fill(exact, (byte)' '); encoded.CopyTo(exact, 0);
        Assert.Equal(payload, StartupHistoryFormat.Read(exact));
        Assert.Throws<JsonException>(() => StartupHistoryFormat.Read([.. exact, (byte)' ']));
        // Literal, nonexistent paths: this exercises bounded encoding, not filesystem IO.
        var suffix = string.Join('\\', Enumerable.Repeat(new string('界', 38), 98));
        var binding = payload.Binding with
        {
            Root = payload.Binding.Root with { Path = @"C:\root\" + suffix },
            Source = payload.Binding.Source with { Directory = payload.Binding.Source.Directory with { Path = @"C:\source\" + suffix } },
            ArchiveBase = payload.Binding.ArchiveBase with { Path = @"C:\archive\" + suffix },
            Archive = payload.Binding.Archive with { Path = @"C:\archive\" + suffix + @"\startup" },
        };
        Assert.Throws<JsonException>(() => StartupHistoryFormat.Write(payload with { Binding = binding }));
        AssertHistoryNoAuthority(new("unverified", null), history);
    }

    [Fact]
    public void HistoryCurrentUserProtectionCannotBindAForeignDeclaredActor()
    {
        using var history = new HistoryFixture(); var payload = history.Payload;
        var sid = payload.Binding.ActorSid == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18";
        var binding = payload.Binding with { ActorSid = sid, Source = payload.Binding.Source with { UserSid = sid } };
        Assert.Throws<CryptographicException>(() => StartupHistoryProtection.Protect(payload with { Binding = binding }));
        AssertHistoryNoAuthority(new("unverified", null), history);
    }

    [Theory]
    [InlineData("root-case")] [InlineData("source-case")] [InlineData("archive-base-id")]
    [InlineData("leaf-id")] [InlineData("length")]
    public void HistoryContextIncludesDirectoryCaseBaseAndOriginalObjectFacts(string mode)
    {
        using var history = new HistoryFixture(); var payload = history.Payload; var binding = payload.Binding;
        var changedId = binding.Original.Identity with { FileId = new string('a', 32) };
        var current = mode switch
        {
            "root-case" => binding with { Root = binding.Root with { CaseSensitivity = binding.Root.CaseSensitivity == "insensitive" ? "sensitive" : "insensitive" } },
            "source-case" => binding with { Source = binding.Source with { Directory = binding.Source.Directory with { CaseSensitivity = binding.Source.Directory.CaseSensitivity == "insensitive" ? "sensitive" : "insensitive" } } },
            "archive-base-id" => binding with { ArchiveBase = binding.ArchiveBase with { Identity = changedId } },
            "leaf-id" => binding with { Original = binding.Original with { Identity = changedId } },
            _ => binding with { Original = binding.Original with { Length = binding.Original.Length + 1 } },
        };
        var cipher = StartupHistoryProtection.Protect(payload);
        var result = StartupHistoryProtection.Inspect(cipher, current, HistoryLatest(payload, cipher));
        Assert.Equal("history_context_changed", result.Reason); AssertHistoryNoAuthority(result, history);
    }

    // Controlled stand-in for an INDEPENDENT protected latest-state authority. Computing
    // this fixture value is not a production authority or evidence of native success.
    private static StartupHistoryLatest HistoryLatest(StartupHistoryPayload payload, byte[] cipher) =>
        new(payload.Sequence, payload.Nonce, payload.Phase, Convert.ToHexStringLower(SHA256.HashData(cipher)));
    private static string DifferentHistoryVolume(string value) => value == new string('a', 16) ? new string('b', 16) : new string('a', 16);
    private static void AssertHistoryNoAuthority(StartupHistoryCheck result, HistoryFixture history)
    {
        Assert.False(result.NativeWriteReady); Assert.False(result.NativeWritePerformed); Assert.False(result.ExecutionRequested);
        Assert.Equal(0, history.Owned.Native.WritableOpens); Assert.Empty(history.Owned.Operations.Calls);
        Assert.False(File.Exists(history.Owned.Paths.StartupBackupPath)); Assert.False(File.Exists(history.Owned.Paths.StartupBackupPath + ".transaction"));
    }
    private static StartupHistoryDirectory HistoryDirectory(Fixture owned, string path)
    {
        if (path != owned.Root) { VerifyEvidenceOwnedPath(owned, path); }
        var observed = StartupNativeSlotProbe.ObserveDirectory(path, default, () => true);
        Assert.Equal("observed", observed.State); Assert.NotNull(observed.DirectoryIdentity);
        return new(path, observed.DirectoryIdentity, observed.CaseSensitivity);
    }
    private sealed class HistoryFixture : IDisposable
    {
        internal Fixture Owned { get; } = new();
        internal StartupHistoryPayload Payload { get; }
        internal StartupBackupRecord Record { get; }
        internal HistoryFixture(string source = "Startup", bool committed = true)
        {
            try
            {
                var origin = Path.Combine(Owned.Native.GetShortcutDirectory(source), "owned.lnk"); VerifyEvidenceOwnedPath(Owned, origin);
                File.WriteAllBytes(origin, [1, 2, 3]); var archiveBase = Path.Combine(Owned.Root, "history-archive");
                var archive = Path.Combine(archiveBase, "startup"); Directory.CreateDirectory(archive);
                using var manager = Owned.Manager(); using var input = manager.CaptureShortcutForDisable(source, "owned");
                var id = Guid.NewGuid().ToString("N"); var when = DateTimeOffset.UtcNow;
                Record = new() { Id = id, Source = source, Name = "owned", Origin = origin, BackupPath = Path.Combine(archive, id + ".lnk"), DisabledAt = when, Disposition = "disabled" };
                var scope = input.Scope; var facts = input.Input;
                var binding = new StartupHistoryBinding(scope.ObserverSid!, HistoryDirectory(Owned, Owned.Root),
                    new(source, scope.SourceKind, scope.KnownFolder, scope.SourceUserSid, new(scope.DirectoryPath!, scope.DirectoryIdentity!, scope.CaseSensitivity)),
                    HistoryDirectory(Owned, archiveBase), HistoryDirectory(Owned, archive), id, Record.Name, StartupHistoryFormat.RecordDigest(Record),
                    new(facts.FileIdentity, facts.Length, facts.LinkCount, facts.Sha256));
                Payload = new(1, committed ? "committed" : "candidate", binding, Guid.NewGuid().ToString("N"), 1, "disabled", when, committed ? when.AddMilliseconds(1) : null);
            }
            catch { Owned.Dispose(); throw; }
        }
        public void Dispose() => Owned.Dispose();
    }
}
