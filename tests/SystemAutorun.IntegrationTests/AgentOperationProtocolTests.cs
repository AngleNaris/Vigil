using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentOperationProtocolTests
{
    [Theory]
    [InlineData("unknown", false)] [InlineData("unknown", true)]
    [InlineData("completed", false)] [InlineData("completed", true)]
    [InlineData("omitted", false)] [InlineData("omitted", true)]
    [InlineData("running", false)] [InlineData("running", true)]
    [InlineData("null", false)] [InlineData("null", true)]
    [InlineData("wrong-id", false)] [InlineData("wrong-id", true)]
    [InlineData("schema", false)] [InlineData("schema", true)]
    [InlineData("numeric-state", false)] [InlineData("numeric-state", true)]
    [InlineData("missing-terminal", false)] [InlineData("missing-terminal", true)]
    [InlineData("wrong-historical-operation", false)] [InlineData("wrong-historical-operation", true)]
    [InlineData("missing-data", false)] [InlineData("missing-data", true)]
    [InlineData("duplicate-data", false)] [InlineData("duplicate-data", true)]
    [InlineData("other-command", false)] [InlineData("other-command", true)]
    [InlineData("not-found", false)] [InlineData("not-found", true)]
    [InlineData("store-corrupt", false)] [InlineData("store-corrupt", true)]
    [InlineData("older-history", false)] [InlineData("older-history", true)]
    public async Task BothClientsValidateOriginalReadIdentityWithoutCreatingAnOperation(string mode, bool cli)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-operation-protocol-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var config = Path.Combine(root, "autorun.yaml");
            const string yaml = "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n";
            await File.WriteAllTextAsync(config, yaml);
            var endpoint = AgentProtocol.ForRoot(root); var id = AgentProtocol.NewOperationId();
            await using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serving = Serve();
            var valid = mode is "unknown" or "completed" or "omitted" or "running" or "null" or "older-history" || mode == "other-command" && cli;
            try
            {
                if (cli)
                {
                    var output = new StringWriter(); var error = new StringWriter();
                    var exit = await new CliApplication().RunAsync(["--root", root, "operation", "show", id.ToUpperInvariant(), "--json"],
                        new StringReader(""), output, error, deadline.Token);
                    Assert.Equal(mode is "not-found" or "store-corrupt" ? 1 : valid ? 0 : 3, exit); Assert.Empty(error.ToString());
                    using var document = JsonDocument.Parse(output.ToString());
                    Assert.Null(document.RootElement.GetProperty("operationId").GetString());
                    if (mode == "older-history")
                    { Assert.Equal(1, document.RootElement.GetProperty("data").GetProperty("response").GetProperty("protocolVersion").GetInt32()); }
                    if (!valid)
                    {
                        Assert.Equal(mode == "not-found" ? "operation_not_found" : mode == "store-corrupt" ? "operation_store_corrupt" : "protocol_mismatch",
                            document.RootElement.GetProperty("error").GetProperty("code").GetString());
                        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
                    }
                }
                else
                {
                    var client = new AgentStartupClient(new AgentClient(root));
                    if (valid || mode == "not-found")
                    {
                        var receipt = await client.ReadAdoptionOperationAsync(id, deadline.Token);
                        if (mode is "null" or "not-found") { Assert.Null(receipt); }
                        else
                        {
                            Assert.Equal(id, receipt!.OperationId);
                            if (mode == "older-history") { Assert.Equal(1, receipt.Response!.ProtocolVersion); }
                        }
                    }
                    else
                    {
                        var error = await Assert.ThrowsAsync<AgentCommandException>(() => client.ReadAdoptionOperationAsync(id, deadline.Token));
                        Assert.Equal(mode == "store-corrupt" ? "operation_store_corrupt" : "protocol_mismatch", error.Code);
                    }
                }
                await serving.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(config, Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)));
                Assert.Equal(yaml, await File.ReadAllTextAsync(config));
            }
            finally
            {
                deadline.Cancel(); await pipe.DisposeAsync();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }

            async Task Serve()
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(deadline.Token))!, AgentProtocol.JsonOptions)!;
                Assert.Equal("operation", request.Command); Assert.Equal(id, request.Name);
                Assert.Null(request.OperationId); Assert.Null(request.ExpectedRevision); Assert.Null(request.Payload);
                var state = mode == "running" ? OperationState.Running : mode == "unknown" ? OperationState.Unknown : OperationState.Completed;
                var receipt = new OperationReceipt(OperationReceipt.CurrentSchemaVersion, id, mode == "other-command" ? "stop" : "startup-adopt", null,
                    new string('a', 64), "previous-agent-instance", state, DateTimeOffset.UtcNow,
                    state == OperationState.Completed ? DateTimeOffset.UtcNow : null,
                    state == OperationState.Completed && mode != "omitted" ? new("original-mutation-request", true, new { opaque = true }, OperationId: id) : null,
                    mode == "omitted");
                var data = JsonSerializer.SerializeToNode(receipt, AgentProtocol.JsonOptions)!;
                if (mode == "wrong-id") { data["operationId"] = AgentProtocol.NewOperationId(); }
                if (mode == "schema") { data["schemaVersion"] = 2; }
                if (mode == "numeric-state") { data["state"] = 2; }
                if (mode == "missing-terminal") { data["completedAt"] = null; }
                if (mode == "wrong-historical-operation") { data["response"]!["operationId"] = AgentProtocol.NewOperationId(); }
                if (mode == "older-history") { data["response"]!["protocolVersion"] = 1; }
                var commandError = mode is "not-found" or "store-corrupt";
                var response = JsonSerializer.SerializeToNode(new AgentResponse(request.Id, !commandError, mode == "null" ? null : data,
                    Error: commandError ? "fixture error with untrusted carried receipt" : null,
                    ErrorCode: mode == "not-found" ? "operation_not_found" : mode == "store-corrupt" ? "operation_store_corrupt" : null,
                    RootId: endpoint.RootId), AgentProtocol.JsonOptions)!;
                if (mode == "missing-data") { response.AsObject().Remove("data"); }
                var wire = response.ToJsonString();
                if (mode == "duplicate-data") { wire = wire[..^1] + ",\"Data\":null}"; }
                await writer.WriteLineAsync(wire.AsMemory(), deadline.Token);
            }
        }
        finally
        {
            var path = Path.GetFullPath(root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(path).StartsWith("SystemAutorun-operation-protocol-", StringComparison.Ordinal)
                || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException("Refusing to remove non-owned operation fixture."); }
            Directory.Delete(path, recursive: true);
        }
    }
}
