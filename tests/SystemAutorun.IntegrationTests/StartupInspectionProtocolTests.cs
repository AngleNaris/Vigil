using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupInspectionControlPlaneTests
{
    [Theory]
    [InlineData("authority", false)] [InlineData("authority", true)]
    [InlineData("action", false)] [InlineData("action", true)]
    [InlineData("missing-flag", false)] [InlineData("missing-flag", true)]
    [InlineData("archive", false)] [InlineData("archive", true)]
    [InlineData("revision", false)] [InlineData("revision", true)]
    [InlineData("unrequested-preview", false)] [InlineData("unrequested-preview", true)]
    public async Task MalformedActualPipeRepliesAreRejectedByBothTypedClientAndCli(string defect, bool cli)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-inspection-protocol-").FullName;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var configuration = Path.Combine(root, "autorun.yaml");
            const string yaml = "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n";
            await File.WriteAllTextAsync(configuration, yaml);
            var endpoint = AgentProtocol.ForRoot(root);
            await using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var revision = new string('a', 64);
            var serving = Serve();
            try
            {
                if (cli)
                {
                    var result = await Run(root, Entry, revision);
                    Assert.Equal(3, result.Exit);
                    using var json = JsonDocument.Parse(result.Text);
                    Assert.Equal("protocol_mismatch", json.RootElement.GetProperty("error").GetProperty("code").GetString());
                    Assert.Null(json.RootElement.GetProperty("operationId").GetString());
                }
                else
                {
                    var error = await Assert.ThrowsAsync<AgentCommandException>(() => new AgentStartupClient(new AgentClient(root))
                        .InspectAsync(Entry.Id, StartupInspectionRequest.FromEntry(Entry), revision, shutdown.Token));
                    Assert.Equal("protocol_mismatch", error.Code);
                }
                await serving.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(configuration, Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories)));
                Assert.Equal(yaml, await File.ReadAllTextAsync(configuration));
            }
            finally
            {
                shutdown.Cancel(); await pipe.DisposeAsync();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }

            async Task Serve()
            {
                await pipe.WaitForConnectionAsync(shutdown.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(shutdown.Token))!, AgentProtocol.JsonOptions)!;
                Assert.Equal("startup-inspect", request.Command); Assert.Null(request.OperationId); Assert.Equal(revision, request.ExpectedRevision);
                var now = DateTimeOffset.UtcNow;
                var snapshot = new StartupInspectionSnapshot(1, revision, now, now, Entry, Entry.MetadataDigest,
                    "not_applicable", false, false, false, false, StartupInspectionPolicy.Describe(Entry));
                var node = JsonSerializer.SerializeToNode(snapshot, AgentProtocol.JsonOptions)!;
                if (defect == "authority") { node["nativeWriteReady"] = true; }
                else if (defect == "action") { node["actions"]![1]!["state"] = "ready"; }
                else if (defect == "missing-flag") { node.AsObject().Remove("nativeWritePerformed"); }
                else if (defect == "archive") { node["originalBackupState"] = "missing"; }
                else if (defect == "unrequested-preview")
                { node["rebuildPreview"] = JsonSerializer.SerializeToNode(StartupRebuildInputPolicy.Preview(new(@"C:\never-probe.exe")), AgentProtocol.JsonOptions); }
                var response = new AgentResponse(request.Id, true, node, RootId: endpoint.RootId,
                    Revision: defect == "revision" ? new string('b', 64) : revision);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.JsonOptions));
            }
        }
        finally
        {
            var path = Path.GetFullPath(root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("SystemAutorun-inspection-protocol-", StringComparison.Ordinal)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidOperationException("Refusing to remove a non-owned protocol root."); }
            Directory.Delete(path, recursive: true);
        }
    }
}
