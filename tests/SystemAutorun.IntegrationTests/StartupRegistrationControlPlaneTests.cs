using System.IO;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class StartupRegistrationControlPlaneTests
{
    [Theory]
    [InlineData("self")] [InlineData("boot")]
    public async Task RealPipeAndCliReturnFixedGlobalMetadataWithoutAConfigurationMutation(string kind)
    {
        var source = new FixedReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(registrationReader: source);
        var before = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        var typed = await new AgentStartupRegistrationClient(fixture.Client, fixture.Root).ReadAsync(kind);
        Assert.True(typed.Available); Assert.False(typed.Registration!.Registered);
        var (exit, json, errors) = await RunCli(fixture, kind);
        Assert.Equal(0, exit); Assert.Empty(errors);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(fixture.Endpoint.RootId, document.RootElement.GetProperty("rootId").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("operationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("revision").ValueKind);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        Assert.False(File.Exists(fixture.Paths.GuiSettingsPath));
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData("name")] [InlineData("payload")] [InlineData("revision")]
    public async Task UnexpectedMutationOrTargetInputsAreRejectedBeforeReader(string field)
    {
        var source = new FixedReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(registrationReader: source);
        var response = await fixture.Client.SendResponseAsync("startup-self-status", field == "name" ? "another" : null,
            field == "payload" ? new { path = @"\\offline.invalid\forbidden" } : null,
            expectedRevision: field == "revision" ? new string('a', 64) : null);
        Assert.False(response.Ok); Assert.Equal("invalid_argument", response.ErrorCode); Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("--yes")] [InlineData("--scope user")] [InlineData("--file -")]
    [InlineData("--operation-id invalid")] [InlineData("unexpected-name")]
    public async Task CliRejectsUnsupportedOptionsWithoutConnecting(string extra)
    {
        var app = new CliApplication(_ => throw new InvalidOperationException("Must not connect."));
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await app.RunAsync(["startup", "self", "status", .. extra.Split(' '), "--json"], new StringReader(""), output, error);
        Assert.Equal(2, exit); Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString()); Assert.Equal("invalid_argument", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false, 1, "registration_source_unavailable")]
    [InlineData(true, 4, "permission_denied")]
    public async Task SourceFailureIsUnavailableNotUnregistered(bool permission, int code, string reason)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(registrationReader: new FailingReader(permission));
        var (exit, text, _) = await RunCli(fixture, "boot"); Assert.Equal(code, exit);
        using var json = JsonDocument.Parse(text);
        var data = json.RootElement.GetProperty("data"); Assert.False(data.GetProperty("available").GetBoolean());
        Assert.Equal(reason, data.GetProperty("reason").GetString()); Assert.Equal(JsonValueKind.Null, data.GetProperty("registration").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("observedAt").ValueKind);
        Assert.DoesNotContain("private-source-value", text);
    }

    [Fact]
    public async Task NativeTimeoutKeepsSharedSlotAndTokenAliveUntilActualCompletion()
    {
        var source = new BlockingReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(registrationReader: source);
        var client = new AgentStartupRegistrationClient(fixture.Client, fixture.Root);
        try
        {
            var boot = client.ReadAsync("boot"); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("registration_busy", (await client.ReadAsync("self")).Reason);
            Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("registration_timeout", (await boot).Reason);
            Assert.Equal("registration_busy", (await client.ReadAsync("self")).Reason); Assert.Equal(1, source.Calls);
            Assert.True(source.Token.WaitHandle.WaitOne(0)); // CTS must not be disposed while native work is still alive.
            source.Release.TrySetResult(new(false));
            StartupRegistrationSnapshot? after = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                after = await client.ReadAsync("self"); if (after.Available) { break; }
                await Task.Delay(10);
            }
            Assert.True(after!.Available); Assert.False(after.Registration!.Registered);
        }
        finally { source.Release.TrySetResult(new(false)); }
    }

    private static async Task<(int Exit, string Output, string Error)> RunCli(ConfigurationControlPlaneTests.ServerFixture fixture, string kind)
    {
        var app = new CliApplication(_ => new PipeTransport(fixture.Client), (_, _) => throw new InvalidOperationException("No implicit Agent start."));
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await app.RunAsync(["--root", fixture.Root, "startup", kind, "status", "--json"], new StringReader(""), output, error);
        return (exit, output.ToString(), error.ToString());
    }
    private sealed class PipeTransport(AgentClient client) : ICliTransport
    {
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken) =>
            client.SendResponseAsync(request.Command, request.Name, request.Payload, request.Timeout, cancellationToken, request.Revision, request.OperationId);
    }
    private sealed class FixedReader : IStartupRegistrationReader
    {
        public int Calls;
        public Task<StartupRegistrationData> ReadAsync(string kind, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new StartupRegistrationData(false)); }
    }
    private sealed class FailingReader(bool permission) : IStartupRegistrationReader
    {
        public Task<StartupRegistrationData> ReadAsync(string kind, CancellationToken cancellationToken = default)
        {
            if (permission) { throw new UnauthorizedAccessException("private-source-value"); }
            throw new IOException("private-source-value");
        }
    }
    private sealed class BlockingReader : IStartupRegistrationReader
    {
        public int Calls; public CancellationToken Token;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<StartupRegistrationData> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StartupRegistrationData> ReadAsync(string kind, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); Token = cancellationToken; Entered.TrySetResult(); return Release.Task; }
    }
}
