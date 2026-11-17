using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Startup", true)]
    [InlineData("Common Startup", false)] [InlineData("Common Startup", true)]
    public void HistoryOperationCodecCorrelatesGivenProtectedEvidenceWithoutIssuingOrPublishingAnything(string source, bool committed)
    {
        using var history = new HistoryFixture(source, committed: false); var candidate = history.Payload;
        var current = OperationCurrent(candidate, committed); var candidateBytes = StartupHistoryProtection.Protect(candidate);
        var currentBytes = committed ? StartupHistoryProtection.Protect(current) : candidateBytes;
        var link = OperationLink(candidate, candidateBytes); var images = OperationImages(history);
        var json = StartupHistoryOperationFormat.Write(link); Assert.Equal(link, StartupHistoryOperationFormat.Read(json));
        var cipher = StartupHistoryProtection.ProtectOperationLink(link); var original = cipher.ToArray();
        Assert.Equal(link, StartupHistoryProtection.UnprotectOperationLink(cipher)); Assert.True(cipher.AsSpan().IndexOf(json) < 0);
        // Controlled independent INDEX fixture, not a real stored index or proof of native success.
        var index = new StartupHistoryIndex(1, candidate.Binding.ActorSid, 1, Guid.NewGuid().ToString("N"), [new(candidate.Binding, HistoryLatest(current, currentBytes))]);
        var indexBytes = StartupHistoryProtection.ProtectIndex(index); var latest = Assert.Single(StartupHistoryProtection.UnprotectIndex(indexBytes).Entries).Latest;
        var result = StartupHistoryProtection.InspectOperationLink(cipher, link.Reference, candidateBytes, currentBytes, current.Binding, latest);
        Assert.Equal(committed ? "associated_committed" : "associated_candidate", result.State); Assert.Equal("history_correlation_only", result.Reason);
        Assert.Equal(link.TransactionId, result.TransactionId); Assert.Equal(candidate.Binding.RecordId, result.RecordId);
        Assert.Equal(original, cipher); Assert.Equal(candidate, StartupHistoryProtection.Unprotect(candidateBytes));
        Assert.Equal(current, StartupHistoryProtection.Unprotect(currentBytes)); var reloaded = StartupHistoryProtection.UnprotectIndex(indexBytes);
        Assert.Equal(index.SchemaVersion, reloaded.SchemaVersion); Assert.Equal(index.ActorSid, reloaded.ActorSid);
        Assert.Equal(index.Generation, reloaded.Generation); Assert.Equal(index.Nonce, reloaded.Nonce); Assert.Equal(index.Entries, reloaded.Entries);
        AssertOperationUnchanged(history, images);
    }

    [Theory]
    [InlineData("version")] [InlineData("op-v4")] [InlineData("op-upper")] [InlineData("op-empty")]
    [InlineData("fingerprint")] [InlineData("fingerprint-upper")] [InlineData("transaction")] [InlineData("candidate-digest")]
    [InlineData("candidate-phase")] [InlineData("candidate-sequence")] [InlineData("null-candidate")]
    [InlineData("missing")] [InlineData("unknown")] [InlineData("nested-unknown")] [InlineData("case-alias")]
    [InlineData("null-reference")] [InlineData("candidate-schema2")]
    public void HistoryOperationStrictFormatRejectsMalformedOrUnversionedAssociations(string mode)
    {
        using var history = new HistoryFixture(committed: false); var candidateBytes = StartupHistoryProtection.Protect(history.Payload);
        var link = OperationLink(history.Payload, candidateBytes); var images = OperationImages(history);
        var root = JsonNode.Parse(StartupHistoryOperationFormat.Write(link))!.AsObject();
        switch (mode)
        {
            case "version": root["schemaVersion"] = 2; break;
            case "op-v4": root["operationId"] = Guid.NewGuid().ToString("N"); break;
            case "op-upper": root["operationId"] = "00112233445576778899AABBCCDDEEFF"; break;
            case "op-empty": root["operationId"] = new string('0', 32); break;
            case "fingerprint": root["fingerprint"] = "short"; break;
            case "fingerprint-upper": root["fingerprint"] = new string('A', 64); break;
            case "transaction": root["transactionId"] = "../other"; break;
            case "candidate-digest": root["candidateProtectedDigest"] = new string('G', 64); break;
            case "candidate-phase": root["candidate"]!["phase"] = "committed"; root["candidate"]!["committedAt"] = history.Payload.CandidateAt; break;
            case "candidate-sequence": root["candidate"]!["sequence"] = 2; break;
            case "null-candidate": root["candidate"] = null; break;
            case "missing": root.Remove("transactionId"); break;
            case "unknown": root["nativeWriteReady"] = true; break;
            case "nested-unknown": root["candidate"]!["binding"]!["root"]!["other"] = "untrusted"; break;
            case "case-alias": root.Remove("operationId"); root["OperationId"] = link.OperationId; break;
            case "null-reference": root["fingerprint"] = null; break;
            case "candidate-schema2": root["candidate"]!["schemaVersion"] = 2; break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        Assert.Throws<JsonException>(() => StartupHistoryOperationFormat.Read(Encoding.UTF8.GetBytes(root.ToJsonString())));
        AssertOperationUnchanged(history, images);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryOperationStrictFormatRejectsDuplicatePropertiesInBothDomains(bool nested)
    {
        using var history = new HistoryFixture(committed: false); var link = OperationLink(history.Payload, StartupHistoryProtection.Protect(history.Payload));
        var text = Encoding.UTF8.GetString(StartupHistoryOperationFormat.Write(link));
        text = nested ? text.Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1", StringComparison.Ordinal)
            : text.Replace("\"operationId\":", "\"operationId\":\"" + link.OperationId + "\",\"operationId\":", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => StartupHistoryOperationFormat.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("operation")] [InlineData("fingerprint")] [InlineData("transaction")]
    [InlineData("record")] [InlineData("root-id")] [InlineData("source-id")] [InlineData("archive-id")] [InlineData("actor")]
    public void HistoryOperationReferenceAndEveryPhysicalContextMustMatch(string mode)
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var candidateBytes = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, candidateBytes); var cipher = StartupHistoryProtection.ProtectOperationLink(link); var expected = link.Reference;
        var current = OperationCurrent(candidate, committed: true); var changed = current.Binding;
        switch (mode)
        {
            case "operation": expected = expected with { OperationId = Guid.CreateVersion7().ToString("N") }; break;
            case "fingerprint": expected = expected with { Fingerprint = new string('b', 64) }; break;
            case "transaction": expected = expected with { TransactionId = Guid.NewGuid().ToString("N") }; break;
            case "record": changed = changed with { RecordId = Guid.NewGuid().ToString("N") }; break;
            case "root-id": changed = changed with { Root = changed.Root with { Identity = changed.Root.Identity with { FileId = new string('a', 32) } } }; break;
            case "source-id": changed = changed with { Source = changed.Source with { Directory = changed.Source.Directory with { Identity = changed.Source.Directory.Identity with { FileId = new string('a', 32) } } } }; break;
            case "archive-id": changed = changed with { Archive = changed.Archive with { Identity = changed.Archive.Identity with { FileId = new string('a', 32) } } }; break;
            case "actor":
                var sid = changed.ActorSid == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18";
                changed = changed with { ActorSid = sid, Source = changed.Source with { UserSid = sid } }; break;
        }
        // Same-actor changed proofs pass the outer latest check, then fail Link-vs-current.
        // A foreign actor cannot be issued by this account; keep that outer rejection separate.
        if (mode is "record" or "root-id" or "source-id" or "archive-id") { current = current with { Binding = changed }; }
        var images = OperationImages(history); var currentBytes = StartupHistoryProtection.Protect(current);
        var result = StartupHistoryProtection.InspectOperationLink(cipher, expected, candidateBytes, currentBytes, changed, HistoryLatest(current, currentBytes));
        Assert.Equal("unverified", result.State);
        Assert.Equal(mode is "operation" or "fingerprint" or "transaction" ? "history_operation_reference_changed"
            : mode == "actor" ? "history_context_changed" : "history_operation_context_changed", result.Reason);
        Assert.Null(result.RecordId); Assert.Null(result.TransactionId); AssertOperationUnchanged(history, images);
    }

    [Theory]
    [InlineData("nonce")] [InlineData("candidate-time")] [InlineData("record-digest")] [InlineData("leaf-id")]
    [InlineData("sequence")] [InlineData("reprotected-parent")]
    public void HistoryOperationCannotSubstituteAnotherCandidateEvenWithTheSameSlotAndLatestPhase(string mode)
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var original = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, original); var other = mode switch
        {
            "nonce" => candidate with { Nonce = Guid.NewGuid().ToString("N") },
            "candidate-time" => candidate with { CandidateAt = candidate.CandidateAt.AddTicks(1) },
            "record-digest" => candidate with { Binding = candidate.Binding with { RecordDigest = new string('b', 64) } },
            "leaf-id" => candidate with { Binding = candidate.Binding with { Original = candidate.Binding.Original with { Identity = candidate.Binding.Original.Identity with { FileId = new string('a', 32) } } } },
            "sequence" => candidate with { Sequence = 2 },
            _ => candidate,
        };
        var parentBytes = StartupHistoryProtection.Protect(other); var current = OperationCurrent(candidate, committed: true); var currentBytes = StartupHistoryProtection.Protect(current);
        var images = OperationImages(history); var result = StartupHistoryProtection.InspectOperationLink(StartupHistoryProtection.ProtectOperationLink(link),
            link.Reference, parentBytes, currentBytes, current.Binding, HistoryLatest(current, currentBytes));
        Assert.Equal("unverified", result.State); Assert.Equal("history_operation_candidate_bytes_changed", result.Reason);
        Assert.Null(result.RecordId); Assert.Null(result.TransactionId);
        // Also compare metadata directly: all differing candidates must fail independent of ciphertext.
        if (mode != "reprotected-parent")
        {
            var direct = StartupHistoryOperationFormat.Match(link, link.Reference, other, current);
            Assert.Equal("unverified", direct.State); Assert.Equal("history_operation_candidate_changed", direct.Reason);
        }
        AssertOperationUnchanged(history, images);
    }

    [Theory]
    [InlineData("sequence")] [InlineData("candidate-time")] [InlineData("candidate-nonce")]
    public void HistoryOperationCommittedTransitionMustContinueTheExactCandidate(string mode)
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var parent = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, parent); var current = mode == "candidate-nonce" ? candidate with { Nonce = Guid.NewGuid().ToString("N") }
            : OperationCurrent(candidate, committed: true);
        if (mode == "sequence") { current = current with { Sequence = 3 }; }
        if (mode == "candidate-time") { current = current with { CandidateAt = candidate.CandidateAt.AddTicks(1) }; }
        var bytes = StartupHistoryProtection.Protect(current); var result = StartupHistoryProtection.InspectOperationLink(StartupHistoryProtection.ProtectOperationLink(link),
            link.Reference, parent, bytes, candidate.Binding, HistoryLatest(current, bytes));
        Assert.Equal("unverified", result.State);
        Assert.Equal(mode == "candidate-nonce" ? "history_operation_candidate_changed" : "history_operation_transition_changed", result.Reason);
        Assert.Null(result.RecordId); Assert.Null(result.TransactionId);
    }

    [Fact]
    public void HistoryOperationPureComparisonAlsoRejectsForeignActorClaimsWithoutIssuingTheirCiphertext()
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload;
        var parent = StartupHistoryProtection.Protect(candidate); var link = OperationLink(candidate, parent); var images = OperationImages(history);
        var actor = candidate.Binding.ActorSid == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18";
        var current = OperationCurrent(candidate, committed: true) with { Binding = candidate.Binding with
            { ActorSid = actor, Source = candidate.Binding.Source with { UserSid = actor } } };
        var result = StartupHistoryOperationFormat.Match(link, link.Reference, candidate, current);
        Assert.Equal("unverified", result.State); Assert.Equal("history_operation_context_changed", result.Reason);
        Assert.Null(result.RecordId); Assert.Null(result.TransactionId); AssertOperationUnchanged(history, images);
    }

    [Theory]
    [InlineData("phase")] [InlineData("disposition")]
    public void HistoryOperationComparisonCannotBypassTheExistingV1StageValidator(string field)
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var parent = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, parent); var current = OperationCurrent(candidate, committed: true); var images = OperationImages(history);
        current = field == "phase" ? current with { Phase = "restored" } : current with { Disposition = "restored" };
        Assert.Throws<JsonException>(() => StartupHistoryOperationFormat.Match(link, link.Reference, candidate, current));
        Assert.Throws<JsonException>(() => StartupHistoryProtection.Protect(current)); AssertOperationUnchanged(history, images);
    }

    [Theory]
    [InlineData("missing")] [InlineData("invalid")] [InlineData("nonce")] [InlineData("digest")] [InlineData("phase")]
    public void HistoryOperationCannotTreatItsOwnClaimAsAnIndependentLatestAuthority(string mode)
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var parent = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, parent); var current = OperationCurrent(candidate, committed: true); var bytes = StartupHistoryProtection.Protect(current);
        StartupHistoryLatest? latest = HistoryLatest(current, bytes);
        latest = mode switch { "missing" => null, "invalid" => latest with { Sequence = 0 }, "nonce" => latest with { Nonce = Guid.NewGuid().ToString("N") },
            "digest" => latest with { ProtectedDigest = new string('c', 64) }, _ => latest with { Phase = "candidate" } };
        var result = StartupHistoryProtection.InspectOperationLink(StartupHistoryProtection.ProtectOperationLink(link), link.Reference, parent, bytes, candidate.Binding, latest);
        Assert.Equal("unverified", result.State); Assert.Null(result.RecordId); Assert.Null(result.TransactionId);
    }

    [Theory]
    [InlineData("first")] [InlineData("last")] [InlineData("truncate")]
    public void HistoryOperationAlteredCiphertextCannotMatchOrExposeLinkedIds(string mode)
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var parent = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, parent); var cipher = StartupHistoryProtection.ProtectOperationLink(link);
        if (mode == "first") { cipher[0] ^= 1; } else if (mode == "last") { cipher[^1] ^= 1; } else { cipher = cipher[..^1]; }
        var result = StartupHistoryProtection.InspectOperationLink(cipher, link.Reference, parent, parent, candidate.Binding, HistoryLatest(candidate, parent));
        Assert.Equal("unverified", result.State); Assert.Null(result.RecordId); Assert.Null(result.TransactionId);
    }

    [Fact]
    public void HistoryOperationHasAnIndependentProtectionDomainAndDoesNotUpgradeLegacyFormats()
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var oldJson = StartupHistoryFormat.Write(candidate);
        var parent = StartupHistoryProtection.Protect(candidate); var link = OperationLink(candidate, parent); var linkJson = StartupHistoryOperationFormat.Write(link);
        var cipher = StartupHistoryProtection.ProtectOperationLink(link); var index = new StartupHistoryIndex(1, candidate.Binding.ActorSid, 1, Guid.NewGuid().ToString("N"), []);
        var indexBytes = StartupHistoryProtection.ProtectIndex(index);
        Assert.Throws<CryptographicException>(() => StartupHistoryProtection.UnprotectOperationLink(parent));
        Assert.Throws<CryptographicException>(() => StartupHistoryProtection.UnprotectOperationLink(indexBytes));
        Assert.Throws<CryptographicException>(() => StartupHistoryProtection.Unprotect(cipher));
        Assert.Throws<CryptographicException>(() => StartupHistoryProtection.UnprotectIndex(cipher));
        Assert.Throws<JsonException>(() => StartupHistoryFormat.Read(linkJson)); Assert.Throws<JsonException>(() => StartupHistoryOperationFormat.Read(oldJson));
        Assert.Equal(oldJson, StartupHistoryFormat.Write(StartupHistoryProtection.Unprotect(parent)));
        var upgraded = JsonNode.Parse(oldJson)!; upgraded["schemaVersion"] = 2;
        Assert.Throws<JsonException>(() => StartupHistoryFormat.Read(Encoding.UTF8.GetBytes(upgraded.ToJsonString())));
    }

    [Fact]
    public void HistoryOperationMissingOversizedCanceledAndImpersonatedInputsRemainUnlinkedOrUnverified()
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var parent = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, parent); var cipher = StartupHistoryProtection.ProtectOperationLink(link); var latest = HistoryLatest(candidate, parent);
        var images = OperationImages(history);
        var missing = StartupHistoryProtection.InspectOperationLink([], link.Reference, parent, parent, candidate.Binding, latest);
        Assert.Equal("unlinked", missing.State); Assert.Null(missing.RecordId); Assert.Null(missing.TransactionId);
        Assert.Equal("unverified", StartupHistoryProtection.InspectOperationLink(cipher, link.Reference, [], parent, candidate.Binding, latest).State);
        var excessive = new byte[StartupHistoryProtection.MaximumProtectedBytes + 1];
        Assert.Equal("unverified", StartupHistoryProtection.InspectOperationLink(excessive, link.Reference, parent, parent, candidate.Binding, latest).State);
        Assert.Throws<CryptographicException>(() => StartupHistoryProtection.UnprotectOperationLink(excessive));
        Assert.Throws<JsonException>(() => StartupHistoryOperationFormat.Read(new byte[StartupHistoryOperationFormat.MaximumBytes + 1]));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StartupHistoryProtection.ProtectOperationLink(link, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => StartupHistoryProtection.UnprotectOperationLink(cipher, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => StartupHistoryProtection.InspectOperationLink(cipher, link.Reference, parent, parent, candidate.Binding, latest, canceled.Token));
        using var identity = WindowsIdentity.GetCurrent(); WindowsIdentity.RunImpersonated(identity.AccessToken, () =>
        {
            Assert.Throws<CryptographicException>(() => StartupHistoryProtection.ProtectOperationLink(link));
            Assert.Throws<CryptographicException>(() => StartupHistoryProtection.UnprotectOperationLink(cipher));
            Assert.Equal("unverified", StartupHistoryProtection.InspectOperationLink(cipher, link.Reference, parent, parent, candidate.Binding, latest).State);
        });
        AssertOperationUnchanged(history, images);
    }

    [Fact]
    public void HistoryOperationPlainAndProtectedInputBoundsDoNotChangeTheCandidate()
    {
        using var history = new HistoryFixture(committed: false); var candidate = history.Payload; var parent = StartupHistoryProtection.Protect(candidate);
        var link = OperationLink(candidate, parent); var encoded = StartupHistoryOperationFormat.Write(link); var exact = new byte[StartupHistoryOperationFormat.MaximumBytes];
        Array.Fill(exact, (byte)' '); encoded.CopyTo(exact, 0); Assert.Equal(link, StartupHistoryOperationFormat.Read(exact));
        Assert.Throws<JsonException>(() => StartupHistoryOperationFormat.Read([.. exact, (byte)' ']));
        var suffix = string.Join('\\', Enumerable.Repeat(new string('界', 38), 98)); var binding = candidate.Binding with
        {
            Root = candidate.Binding.Root with { Path = @"C:\root\" + suffix },
            Source = candidate.Binding.Source with { Directory = candidate.Binding.Source.Directory with { Path = @"C:\source\" + suffix } },
            ArchiveBase = candidate.Binding.ArchiveBase with { Path = @"C:\archive\" + suffix },
            Archive = candidate.Binding.Archive with { Path = @"C:\archive\" + suffix + @"\startup" },
        };
        Assert.Throws<JsonException>(() => StartupHistoryOperationFormat.Write(link with { Candidate = candidate with { Binding = binding } }));
        Assert.Equal(candidate, StartupHistoryProtection.Unprotect(parent));
    }

    [Fact]
    public void HistoryOperationAdditionKeepsBothLiteralV1JsonContracts()
    {
        const string volume = "0000000000000001"; var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var root = new StartupHistoryDirectory(@"C:\owned-root", new(volume, new string('1', 32)), "insensitive");
        var source = new StartupHistorySource("Common Startup", "machine", "CommonStartup", null,
            new(@"C:\owned-source", new(volume, new string('2', 32)), "insensitive"));
        var archiveBase = new StartupHistoryDirectory(@"C:\owned-archive", new(volume, new string('3', 32)), "insensitive");
        var archive = new StartupHistoryDirectory(@"C:\owned-archive\startup", new(volume, new string('4', 32)), "insensitive");
        var binding = new StartupHistoryBinding("S-1-5-18", root, source, archiveBase, archive, new string('5', 32), "owned", new string('a', 64),
            new(new(volume, new string('6', 32)), 3, 1, new string('b', 64)));
        var payload = new StartupHistoryPayload(1, "candidate", binding, new string('7', 32), 1, "disabled", when, null);
        var expected = $$$"""{"schemaVersion":1,"phase":"candidate","binding":{"actorSid":"S-1-5-18","root":{"path":"C:\\owned-root","identity":{"volumeSerialNumber":"0000000000000001","fileId":"{{{new string('1', 32)}}}"},"caseSensitivity":"insensitive"},"source":{"source":"Common Startup","kind":"machine","knownFolder":"CommonStartup","userSid":null,"directory":{"path":"C:\\owned-source","identity":{"volumeSerialNumber":"0000000000000001","fileId":"{{{new string('2', 32)}}}"},"caseSensitivity":"insensitive"}},"archiveBase":{"path":"C:\\owned-archive","identity":{"volumeSerialNumber":"0000000000000001","fileId":"{{{new string('3', 32)}}}"},"caseSensitivity":"insensitive"},"archive":{"path":"C:\\owned-archive\\startup","identity":{"volumeSerialNumber":"0000000000000001","fileId":"{{{new string('4', 32)}}}"},"caseSensitivity":"insensitive"},"recordId":"{{{new string('5', 32)}}}","name":"owned","recordDigest":"{{{new string('a', 64)}}}","original":{"identity":{"volumeSerialNumber":"0000000000000001","fileId":"{{{new string('6', 32)}}}"},"length":3,"linkCount":1,"sha256":"{{{new string('b', 64)}}}"}},"nonce":"{{{new string('7', 32)}}}","sequence":1,"disposition":"disabled","candidateAt":"2026-01-01T00:00:00+00:00","committedAt":null}""";
        var encoded = StartupHistoryFormat.Write(payload); Assert.Equal(Encoding.UTF8.GetBytes(expected), encoded);
        Assert.Equal(payload, StartupHistoryFormat.Read(encoded));
        var link = new StartupHistoryOperationLink(1, "00112233445576778899aabbccddeeff", new string('a', 64),
            "8899aabbccdd4eef8899aabbccddeeff", new string('c', 64), payload);
        var expectedLink = "{\"schemaVersion\":1,\"operationId\":\"00112233445576778899aabbccddeeff\",\"fingerprint\":\"" + new string('a', 64)
            + "\",\"transactionId\":\"8899aabbccdd4eef8899aabbccddeeff\",\"candidateProtectedDigest\":\"" + new string('c', 64) + "\",\"candidate\":" + expected + "}";
        var operation = StartupHistoryOperationFormat.Write(link); Assert.Equal(Encoding.UTF8.GetBytes(expectedLink), operation);
        Assert.Equal(link, StartupHistoryOperationFormat.Read(operation));
    }

    private static StartupHistoryOperationLink OperationLink(StartupHistoryPayload candidate, byte[] candidateBytes) => new(1,
        Guid.CreateVersion7().ToString("N"), new string('a', 64), Guid.CreateVersion7().ToString("N"), Convert.ToHexStringLower(SHA256.HashData(candidateBytes)), candidate);
    private static StartupHistoryPayload OperationCurrent(StartupHistoryPayload candidate, bool committed) => committed
        ? candidate with { Phase = "committed", Sequence = 2, Nonce = Guid.NewGuid().ToString("N"), CommittedAt = candidate.CandidateAt.AddMilliseconds(1) } : candidate;
    private static Dictionary<string, string> OperationImages(HistoryFixture history) => Directory.GetFiles(history.Owned.Root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    private static void AssertOperationUnchanged(HistoryFixture history, Dictionary<string, string> images)
    {
        var current = OperationImages(history); Assert.Equal(images.Count, current.Count);
        foreach (var pair in images) { Assert.True(current.TryGetValue(pair.Key, out var hash)); Assert.Equal(pair.Value, hash); }
        Assert.Equal(0, history.Owned.Native.WritableOpens); Assert.Empty(history.Owned.Operations.Calls);
        Assert.False(File.Exists(history.Owned.Paths.StartupBackupPath)); Assert.False(File.Exists(history.Owned.Paths.StartupBackupPath + ".transaction"));
    }
}
