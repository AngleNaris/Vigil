using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Tests;

public sealed class EndpointIsolationTests
{
    [Fact]
    public void DifferentRootsUseDifferentAgentAndGuiInstances()
    {
        using var firstRoot = new OwnedRoot();
        using var secondRoot = new OwnedRoot();
        var first = AgentProtocol.ForRoot(firstRoot.Path);
        var second = AgentProtocol.ForRoot(secondRoot.Path);
        Assert.NotEqual(first.RootId, second.RootId);
        Assert.NotEqual(first.PipeName, second.PipeName);
        Assert.NotEqual(first.MutexName, second.MutexName);
        Assert.NotEqual(first.AppInstanceName, second.AppInstanceName);
    }

    [Fact]
    public void WindowsCaseAndTrailingSeparatorDoNotCreateAnotherInstance()
    {
        using var root = new OwnedRoot();
        var path = root.Path;
        var first = AgentProtocol.ForRoot(path);
        var second = AgentProtocol.ForRoot(path.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Assert.Equal(first.RootDirectory, second.RootDirectory);
        Assert.Equal(first.RootId, second.RootId);
        Assert.Equal(first.PipeName, second.PipeName);
        Assert.Contains(".v2.", first.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JunctionUsesSameEndpointAndFilesystemLifetimeLease()
    {
        using var root = new OwnedRoot();
        var alias = Path.Combine(root.Path, "alias");
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, root.Path })
        {
            start.ArgumentList.Add(argument);
        }

        using var command = System.Diagnostics.Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await command.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, command.ExitCode);
            var original = AgentProtocol.ForRoot(root.Path);
            var linked = AgentProtocol.ForRoot(alias);
            Assert.Equal(original, linked);
            Assert.Equal(ProjectPaths.Resolve(root.Path), ProjectPaths.Resolve(alias));
            using var firstLease = RootInstanceLease.TryAcquire(new ProjectPaths(root.Path));
            Assert.NotNull(firstLease);
            using var duplicateLease = RootInstanceLease.TryAcquire(new ProjectPaths(alias));
            Assert.Null(duplicateLease);
        }
        finally
        {
            if (!command.HasExited)
            {
                command.Kill(entireProcessTree: true);
                await command.WaitForExitAsync();
            }

            if (Directory.Exists(alias))
            {
                // Delete only the test-owned junction entry, never its target.
                Assert.Equal(Path.GetFullPath(root.Path), Path.GetDirectoryName(Path.GetFullPath(alias)));
                Directory.Delete(alias);
            }
        }
    }

    [Fact]
    public void ExplicitRootCannotFallBackToParentConfiguration()
    {
        using var root = new OwnedRoot();
        var child = Directory.CreateDirectory(Path.Combine(root.Path, "missing-config"));
        Assert.Throws<DirectoryNotFoundException>(() => ProjectPaths.Resolve(child.FullName));
        Assert.Equal(root.Path, ProjectPaths.Resolve(root.Path).RootDirectory);
    }

    [Fact]
    public void ExplicitEmptyRootCannotFallBackToWorkingDirectory() =>
        Assert.Throws<ArgumentException>(() => ProjectPaths.Resolve(" "));

    [Fact]
    public async Task ClientAcceptsOnlyMatchingRootAndProtocol()
    {
        await ExchangeAsync(matchingRoot: true, matchingVersion: true);
        await ExchangeAsync(matchingRoot: false, matchingVersion: true);
        await ExchangeAsync(matchingRoot: true, matchingVersion: false);
    }

    [Fact]
    public async Task LostMutationAcknowledgementExposesStableOperationIdentity()
    {
        using var root = new OwnedRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endpoint = AgentProtocol.ForRoot(root.Path);
        await using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var operationId = AgentProtocol.NewOperationId();
        var server = DisconnectAsync();
        var unknown = await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() =>
            new AgentClient(root.Path).SendAsync<JsonElement>("stop", "synthetic-only",
                timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token, operationId: operationId));
        Assert.Equal(operationId, unknown.OperationId);
        await server;

        async Task DisconnectAsync()
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(timeout.Token))!, AgentProtocol.JsonOptions)!;
            Assert.Equal(operationId, request.OperationId);
            pipe.Disconnect();
        }
    }

    [Fact]
    public async Task ConnectionTimeoutBeforeWriteDoesNotClaimUnknownExecution()
    {
        using var root = new OwnedRoot();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentClient(root.Path).SendAsync<JsonElement>("stop", "synthetic-only",
                timeout: TimeSpan.FromMilliseconds(100), operationId: AgentProtocol.NewOperationId()));
    }

    private static async Task ExchangeAsync(bool matchingRoot, bool matchingVersion)
    {
        using var root = new OwnedRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endpoint = AgentProtocol.ForRoot(root.Path);
        await using var pipe = new NamedPipeServerStream(
            endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var server = RespondAsync();
        var request = new AgentClient(root.Path).SendAsync<JsonElement>(
            "ping", timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        if (matchingRoot && matchingVersion)
        {
            var result = await request;
            Assert.Equal("ready", result.GetProperty("status").GetString());
        }
        else
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => request);
            Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        }

        await server;

        async Task RespondAsync()
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            var line = await reader.ReadLineAsync(timeout.Token);
            var incoming = JsonSerializer.Deserialize<AgentRequest>(line!, AgentProtocol.JsonOptions)!;
            Assert.Equal(endpoint.RootId, incoming.RootId);
            Assert.Equal(AgentProtocol.Version, incoming.ProtocolVersion);
            var response = new AgentResponse(
                incoming.Id, true, new { status = "ready" },
                ProtocolVersion: matchingVersion ? AgentProtocol.Version : 1,
                RootId: matchingRoot ? endpoint.RootId : "another-root");
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(response, AgentProtocol.JsonOptions).AsMemory(), timeout.Token);
        }
    }

    private sealed class OwnedRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("SystemAutorun-endpoint-tests-").FullName;

        public OwnedRoot() => File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "# isolated test\n");

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(resolved).StartsWith("SystemAutorun-endpoint-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete a non-owned test root.");
            }

            Directory.Delete(resolved, recursive: true);
        }
    }
}
