using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class CliContextHelpTests
{
    public static IEnumerable<object[]> Commands() => CliArguments.Routes.Select(route => new object[] { route.Key });

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task EveryImplementedCommandHasEquivalentLocalTextAndJsonHelp(string command)
    {
        var app = LocalOnly();
        var words = command.Split(' ');
        using var input = new ForbiddenInput();
        using var text = new StringWriter(); using var json = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await app.RunAsync(["help", .. words], input, text, error));
        // Even a nonsensical root and timeout do not reach operational validation during help.
        Assert.Equal(0, await app.RunAsync([.. words, "--help", "--json", "--root", "invalid:root", "--timeout", "NaN"], input, json, error));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(text.ToString().TrimEnd(), document.RootElement.GetProperty("data").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("operationId").ValueKind);
        Assert.Contains("Usage: vigil [--root DIR] " + command, text.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("config apply", "--file YAML", "40 KiB")]
    [InlineData("settings set", "data.preferences", "snake_case")]
    [InlineData("settings patch", "autoRecoverFailedTasks", "camelCase")]
    [InlineData("startup inspect", "--native", "No --yes/--operation-id")]
    [InlineData("startup self set", "ONLY reviewed JSON.data", "never clear or replay")]
    public async Task ContextHelpKeepsInputAndConfirmationContracts(string command, string first, string second)
    {
        using var output = new StringWriter(); using var error = new StringWriter(); using var input = new ForbiddenInput();
        Assert.Equal(0, await LocalOnly().RunAsync(["help", .. command.Split(' ')], input, output, error));
        Assert.Contains(first, output.ToString()); Assert.Contains(second, output.ToString());
        if (command == "startup self set") { Assert.DoesNotContain("Input example (JSON):", output.ToString()); }
        if (command == "startup inspect") { Assert.DoesNotContain("Optional: --operation-id", output.ToString()); }
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("task")]
    [InlineData("startup")]
    [InlineData("startup self")]
    [InlineData("startup boot")]
    [InlineData("startup operation")]
    [InlineData("logs")]
    public async Task GroupsListOnlyRegisteredCommands(string group)
    {
        using var output = new StringWriter(); using var error = new StringWriter(); using var input = new ForbiddenInput();
        Assert.Equal(0, await LocalOnly().RunAsync(["help", .. group.Split(' ')], input, output, error));
        foreach (var route in CliArguments.Routes.Where(route => route.Key.StartsWith(group + " ", StringComparison.Ordinal)))
        { Assert.Contains("  " + route.Key, output.ToString()); }
        Assert.DoesNotContain("  startup disable", output.ToString());
        Assert.DoesNotContain("  startup boot install", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("task remove", "fixture")]
    [InlineData("startup self prepare", "enable")]
    [InlineData("monitor history", "fixture")]
    [InlineData("host status", "fixture")]
    public void ContextWithAnOperandRetainsOnlyCanonicalTopic(string command, string operand)
    {
        var result = CliArguments.Parse([.. command.ToUpperInvariant().Split(' '), operand, "--help"]);
        Assert.Equal("help", result.Route.Key); Assert.Equal(command, result.Name);
    }

    [Theory]
    [InlineData("startup disable")]
    [InlineData("startup boot install")]
    [InlineData("startup self enable")]
    [InlineData("logs pretend")]
    [InlineData("task add extra")]
    [InlineData("monitor history one two")]
    [InlineData("unknown")]
    public async Task UnknownHelpTopicDoesNotAdvertiseAnUnimplementedRoute(string topic)
    {
        using var output = new StringWriter(); using var error = new StringWriter(); using var input = new ForbiddenInput();
        Assert.Equal(2, await LocalOnly().RunAsync(["help", .. topic.Split(' '), "--json"], input, output, error));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean()); Assert.Empty(error.ToString());
        Assert.Equal("invalid_argument", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void LiteralHelpOperandRemainsANameAndNormalMutationGuardsRemain()
    {
        var result = CliArguments.Parse(["task", "show", "--", "--help"]);
        Assert.Equal("task show", result.Route.Key); Assert.Equal("--help", result.Name);
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["task", "remove", "fixture"]));
        Assert.Throws<CliAuthorizationException>(() => CliArguments.Parse(["task", "remove", "fixture", "--expected-revision", new string('a', 64)]));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["task", "list", "--not-an-option", "--help"]));
    }

    [Theory]
    [InlineData("task add", "task-add")]
    [InlineData("settings patch", "settings-patch")]
    [InlineData("host add", "remote-add")]
    [InlineData("host update", "remote-update")]
    [InlineData("host upsert", "remote-upsert")]
    public async Task PrintedJsonExamplesAreAcceptedByTheActualInputPipeline(string command, string rpc)
    {
        using var help = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await LocalOnly().RunAsync(["help", .. command.Split(' ')], new ForbiddenInput(), help, error));
        using var lines = new StringReader(help.ToString());
        string? line; do { line = lines.ReadLine(); } while (line is not null && line != "Input example (JSON):");
        Assert.NotNull(line); var example = lines.ReadLine(); Assert.NotNull(example);
        var transport = new RecordingTransport(); using var root = new TempRoot();
        using var input = new StringReader(example); using var output = new StringWriter();
        Assert.Equal(0, await new CliApplication(_ => transport).RunAsync([.. command.Split(' '), "--root", root.Path, "--file", "-", "--expected-revision", new string('a', 64), "--json"], input, output, error));
        var request = Assert.Single(transport.Requests); Assert.Equal(rpc, request.Command);
        if (rpc == "task-add")
        {
            var task = Assert.IsType<AddTaskRequest>(request.Payload);
            Assert.Equal("example-app", task.Name); Assert.Equal(AutorunTaskType.Windows, task.Type);
            Assert.Equal(@"C:\Apps\Example\Example.exe", task.Executable); Assert.False(task.ExpectedRunning);
        }
        else if (rpc == "settings-patch")
        {
            var patch = Assert.IsType<ApplicationSettingsPatch>(request.Payload);
            Assert.False(patch.AutoRecoverFailedTasks); Assert.False(patch.AutoStartMissingOnLaunch);
        }
        else
        {
            var host = Assert.IsType<RemoteHostDefinition>(request.Payload);
            Assert.False(host.Enabled); Assert.Equal("example-host", host.Id); Assert.Equal("example", host.UserName);
            Assert.Equal(RemoteAuthenticationMode.SshAgent, host.Authentication);
        }
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ConsoleHelpExitsWithStdinStillOpenAndNeverCreatesTheRequestedRoot()
    {
        var executable = typeof(CliContextHelpTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "CliExecutablePath").Value!;
        var root = Path.Combine(Path.GetTempPath(), "SystemAutorun-help-not-created-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(root));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "task", "add", "--help", "--file", "-", "--root", root, "--json" }) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode); Assert.Empty(await error);
            using var document = JsonDocument.Parse(await output);
            Assert.Contains("task add", document.RootElement.GetProperty("data").GetProperty("text").GetString());
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            // A regression must fail the assertion above without leaking its test-owned root.
            if (Directory.Exists(root))
            {
                var directory = new DirectoryInfo(root);
                Assert.Equal(root, directory.FullName, ignoreCase: true);
                Assert.False(directory.Attributes.HasFlag(FileAttributes.ReparsePoint));
                directory.Delete(recursive: true);
            }
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("SystemAutorun-help-example-").FullName;
        public TempRoot() => File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "version: 1\ntasks: []\n");
        public void Dispose()
        {
            var directory = new DirectoryInfo(Path);
            Assert.Equal(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\'), directory.Parent!.FullName.TrimEnd('\\'), ignoreCase: true);
            Assert.StartsWith("SystemAutorun-help-example-", directory.Name, StringComparison.Ordinal);
            Assert.False(directory.Attributes.HasFlag(FileAttributes.ReparsePoint));
            directory.Delete(recursive: true);
        }
    }

    private static CliApplication LocalOnly() => new(_ => throw new InvalidOperationException("Help connected to Agent."),
        (_, _) => throw new InvalidOperationException("Help started Agent."));

    private sealed class ForbiddenInput : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Help read input.");
        public override string ReadToEnd() => throw new InvalidOperationException("Help read input.");
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Help read input.");
    }

    private sealed class RecordingTransport : ICliTransport
    {
        public List<CliRequest> Requests { get; } = [];
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new AgentResponse("help-example", true, new { accepted = true },
                Revision: new string('b', 64)));
        }
    }
}
