using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

// Deliberately synthetic wire/identity data, not evidence of native execution.
public sealed class StartupNativeInspectionProtocolTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static StartupEntry Entry => StartupNativeInspectionControlPlaneTests.Entry;
    private static StartupInspectionRequest Request => StartupInspectionRequest.FromEntry(Entry);
    private static JsonNode ValidData()
    {
        var read = StartupNativeInspectionControlPlaneTests.Evidence(); var now = DateTimeOffset.UtcNow;
        var metadata = new StartupInspectionSnapshot(1, Revision, now, now, Entry, Entry.MetadataDigest, "not_checked", false,
            false, false, false, StartupInspectionPolicy.Describe(Entry));
        return JsonSerializer.SerializeToNode(new StartupNativeInspectionSnapshot(2, Revision, now, metadata, read.Evidence,
            read.RecordMetadataDigestAfter, "sequential-independent", "unverified", false, false, false), AgentProtocol.JsonOptions)!;
    }

    [Fact]
    public void SimulatedCompleteNativeEnvelopeIsAcceptedWithoutChangingTheNestedLegacyMeaning()
    {
        var result = AgentStartupClient.ReadNativeInspection(new("r", true, ValidData(), Revision: Revision), Entry.Id, Request, Revision);
        Assert.Equal(2, result.SchemaVersion); Assert.Equal(1, result.Metadata.SchemaVersion); Assert.Equal("not_checked", result.Metadata.OriginalBackupState);
        Assert.Equal("unverified", result.HistoricalIdentity); Assert.False(result.NativeWriteReady);
    }

    [Theory]
    [InlineData("outer-authority")] [InlineData("slot-authority")] [InlineData("metadata-authority")]
    [InlineData("missing-flag")] [InlineData("missing-slot-flag")] [InlineData("missing-time")]
    [InlineData("cas")] [InlineData("trust")] [InlineData("scope")] [InlineData("schema")]
    [InlineData("digest")] [InlineData("id")] [InlineData("source")] [InlineData("name")]
    [InlineData("stale")] [InlineData("future")] [InlineData("case")] [InlineData("missing-parent")] [InlineData("directory")]
    [InlineData("missing-error")] [InlineData("unknown-reason")] [InlineData("positive-without-file")]
    [InlineData("identity")] [InlineData("length")] [InlineData("action")] [InlineData("entry-computed")]
    [InlineData("revision")] [InlineData("operation")] [InlineData("replayed")] [InlineData("error")]
    public void SimulatedMalformedEvidenceCannotCrossTypedNativeProtocolBoundary(string defect)
    {
        var data = ValidData(); Corrupt(data, defect);
        var response = new AgentResponse("r", true, data, Revision: defect == "revision" ? new string('b', 64) : Revision,
            OperationId: defect == "operation" ? AgentProtocol.NewOperationId() : null,
            Replayed: defect == "replayed", Error: defect == "error" ? "unexpected" : null);
        var error = Assert.Throws<AgentCommandException>(() => AgentStartupClient.ReadNativeInspection(response, Entry.Id, Request, Revision));
        Assert.Equal("protocol_mismatch", error.Code);
    }

    [Theory]
    [InlineData("none")] [InlineData("missing-flag")] [InlineData("cas")] [InlineData("case")] [InlineData("entry-computed")] [InlineData("boundary")]
    public async Task SimulatedMalformedActualPipeResponseIsRejectedWithoutFallbackOrAnyNativeProbe(string defect)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-native-inspection-protocol-").FullName;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var configuration = Path.Combine(root, "autorun.yaml");
            const string yaml = "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n";
            await File.WriteAllTextAsync(configuration, yaml);
            var endpoint = AgentProtocol.ForRoot(root);
            await using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serving = Serve();
            try
            {
                var pending = new AgentStartupClient(new AgentClient(root)).InspectNativeAsync(Entry.Id, Request, Revision, shutdown.Token);
                if (defect == "none") { Assert.Equal(2, (await pending).SchemaVersion); }
                else { Assert.Equal("protocol_mismatch", (await Assert.ThrowsAsync<AgentCommandException>(() => pending)).Code); }
                await serving.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(configuration, Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories)));
                Assert.Equal(yaml, await File.ReadAllTextAsync(configuration));
            }
            finally
            {
                shutdown.Cancel(); await pipe.DisposeAsync();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }
            async Task Serve()
            {
                // Match AgentServer.HandleConnectionAndDisposeAsync: this one-response
                // handler owns completion/EOF, not the client assertion awaiting its reply.
                await using var connectionLifetime = pipe;
                await pipe.WaitForConnectionAsync(shutdown.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(shutdown.Token))!, AgentProtocol.JsonOptions)!;
                Assert.Equal("startup-inspect-native", request.Command); Assert.Null(request.OperationId); Assert.Equal(Revision, request.ExpectedRevision);
                var data = ValidData(); Corrupt(data, defect);
                var response = new AgentResponse(request.Id, true, data, RootId: endpoint.RootId, Revision: Revision);
                if (defect == "boundary") { data["padding"] = ""; }
                var frame = JsonSerializer.Serialize(response, AgentProtocol.JsonOptions);
                if (defect == "boundary")
                {
                    var bytes = Encoding.UTF8.GetByteCount(frame) + Encoding.UTF8.GetByteCount(writer.NewLine);
                    var target = ((bytes + 1023) / 1024) * 1024;
                    if (target % 4096 == 0) { target += 1024; }
                    data["padding"] = new string('x', target - bytes);
                    frame = JsonSerializer.Serialize(response, AgentProtocol.JsonOptions);
                    Assert.Equal(target, Encoding.UTF8.GetByteCount(frame) + Encoding.UTF8.GetByteCount(writer.NewLine));
                    Assert.NotEqual(0, target % 4096);
                }
                await writer.WriteLineAsync(frame);
            }
        }
        finally
        {
            var path = Path.GetFullPath(root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("SystemAutorun-native-inspection-protocol-", StringComparison.Ordinal)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new InvalidOperationException("Non-owned protocol root."); }
            Directory.Delete(path, recursive: true);
        }
    }

    private static void Corrupt(JsonNode data, string defect)
    {
        var slots = data["slots"]!; var original = slots["original"]!; var metadata = data["metadata"]!;
        switch (defect)
        {
            case "outer-authority": data["nativeWriteReady"] = true; break;
            case "slot-authority": slots["nativeWritePerformed"] = true; break;
            case "metadata-authority": metadata["executionRequested"] = true; break;
            case "missing-flag": data.AsObject().Remove("nativeWritePerformed"); break;
            case "missing-slot-flag": slots.AsObject().Remove("executionRequested"); break;
            case "missing-time": original.AsObject().Remove("observedAt"); break;
            case "cas": slots["cas"] = true; break;
            case "trust": data["historicalIdentity"] = "verified"; break;
            case "scope": data["validationScope"] = "atomic"; break;
            case "schema": data["schemaVersion"] = 1; break;
            case "digest": data["recordMetadataDigestAfter"] = new string('b', 64); break;
            case "id": slots["backupId"] = Guid.NewGuid().ToString("N"); break;
            case "source": slots["source"] = "Common Startup"; break;
            case "name": slots["name"] = "changed"; break;
            case "stale": original["observedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "future": data["completedAt"] = DateTimeOffset.UtcNow.AddMinutes(1); break;
            case "case": original["caseSensitivity"] = "sensitive"; break;
            case "missing-parent": original["directoryIdentity"] = null; break;
            case "directory": original["sourceDirectory"] = @"\\offline.invalid\never-probe"; break;
            case "missing-error": original["nativeErrorCode"] = null; break;
            case "unknown-reason": original["state"] = "unknown"; original["reason"] = "restorable"; break;
            case "positive-without-file": original["state"] = "present"; original["nativeErrorCode"] = null; break;
            case "identity": original["directoryIdentity"]!["fileId"] = "bad"; break;
            case "length": original["length"] = 65537; break;
            case "action": metadata["actions"]![3]!["state"] = "ready"; break;
            case "entry-computed": metadata["entry"]!["isManaged"] = true; break;
        }
    }
}
