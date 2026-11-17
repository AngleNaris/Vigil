using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed partial class GuiLogDirectoryTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("relative")]
    [InlineData("absolute")]
    public async Task LiveDirectoryMatchesIndependentConsoleWithoutRebasingGuiEdits(string mode)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var editing = new AgentConfigurationClient(fixture.Client);
        var loaded = await editing.ReadAsync();
        var directory = mode switch
        {
            "relative" => Path.Combine(fixture.Root, "自定义 logs", "with spaces"),
            "absolute" => Path.Combine(fixture.Root, "绝对 logs", "with spaces"),
            _ => Path.Combine(fixture.Root, "logs"),
        };
        var declared = mode switch { "relative" => "自定义 logs/with spaces", "absolute" => directory, _ => null };
        // Simulate the supported external-editor path only in the owned fixture.
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, Yaml(declared));
        var current = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.NotEqual(loaded.Revision, current.Revision);
        Assert.Equal(directory, current.Configuration.Settings.LogDirectory);
        var bytes = Fingerprint(fixture.Root);
        var cli = await ConsolePath(fixture.Root);
        Assert.Equal(directory, cli.Directory); Assert.Equal(fixture.Endpoint.RootId, cli.RootId);
        Assert.False(Directory.Exists(directory)); Assert.Equal(bytes, Fingerprint(fixture.Root));
        var launches = new List<ProcessStartInfo>(); var errors = new List<string>();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture.Client, fixture.Paths, editing, info => { launches.Add(info); return null; }, errors);
            model.SearchText = "保留筛选"; model.CurrentPage = MainWindowViewModel.ExceptionLogsPage;
            try
            {
                await model.OpenLogsCommand.ExecuteAsync(null);
                AssertLaunch(Assert.Single(launches), directory);
                Assert.True(Directory.Exists(directory)); Assert.Empty(errors);
                Assert.Equal("已请求打开日志目录。", model.StatusMessage);
                Assert.Equal(loaded.Revision, editing.Revision); // The lookup must not refresh a stale CAS cursor.
                Assert.Equal("保留筛选", model.SearchText); Assert.Equal(MainWindowViewModel.ExceptionLogsPage, model.CurrentPage);
                Assert.Equal(bytes, Fingerprint(fixture.Root));
                if (mode != "default") { Assert.False(Directory.Exists(Path.Combine(fixture.Root, "logs"))); }
                var nextDirectory = Path.Combine(fixture.Root, "修改后的日志");
                await File.WriteAllTextAsync(fixture.Paths.ConfigPath, Yaml("修改后的日志"));
                var afterEdit = Fingerprint(fixture.Root);
                var next = await ConsolePath(fixture.Root);
                Assert.False(Directory.Exists(nextDirectory)); Assert.Equal(afterEdit, Fingerprint(fixture.Root));
                await model.OpenLogsCommand.ExecuteAsync(null);
                Assert.Equal(2, launches.Count); AssertLaunch(launches[1], next.Directory);
                Assert.Equal(nextDirectory, next.Directory); Assert.True(Directory.Exists(nextDirectory));
                Assert.Equal(loaded.Revision, editing.Revision); Assert.Empty(errors);
                Assert.Equal(afterEdit, Fingerprint(fixture.Root));
                await Evidence($"directory-{mode}.json", new { fixture.Root, fixture.Endpoint.RootId, mode, directory, nextDirectory,
                    firstConsole = cli.Text, secondConsole = next.Text, loadedRevision = loaded.Revision, currentRevision = current.Revision,
                    editingRevisionAfterLookups = editing.Revision, opened = launches.Select(info => info.FileName).ToArray(),
                    configurationFilesUnchangedByLookups = true, actualShellStarted = false, model.StatusMessage });
            }
            finally { model.DetachDashboard(); }
        });
        Assert.Empty(fixture.ObservedErrors);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("directory-conflict")]
    [InlineData("shell-denied")]
    public async Task FailuresNeverOpenFallbackOrRebaseAndExplicitRetryUsesFreshConfiguration(string failure)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var editing = new AgentConfigurationClient(fixture.Client); var loaded = await editing.ReadAsync();
        var directory = Path.Combine(fixture.Root, "失败目录");
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, failure == "configuration" ? "version: [\n" : Yaml("失败目录"));
        if (failure == "directory-conflict") { await File.WriteAllTextAsync(directory, "owned blocking file"); }
        var before = Fingerprint(fixture.Root); var attempts = new List<string>(); var errors = new List<string>();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture.Client, fixture.Paths, editing, info =>
            { attempts.Add(info.FileName); throw new Win32Exception(5, "Synthetic shell denied; no Explorer started."); }, errors);
            try
            {
                await model.OpenLogsCommand.ExecuteAsync(null);
                var error = Assert.Single(errors); Assert.NotEmpty(error);
                Assert.StartsWith("日志目录未打开：", model.StatusMessage);
                Assert.Equal(failure == "shell-denied" ? 1 : 0, attempts.Count);
                Assert.False(Directory.Exists(Path.Combine(fixture.Root, "logs")));
                Assert.Equal(before, Fingerprint(fixture.Root)); Assert.Equal(loaded.Revision, editing.Revision);
                Assert.True(model.OpenLogsCommand.CanExecute(null));
            }
            finally { model.DetachDashboard(); }
            await Evidence($"directory-{failure}.json", new { fixture.Root, failure, attempts, errors,
                editingRevision = editing.Revision, configurationFilesUnchanged = true, fallbackOpened = false, actualShellStarted = false });
            // Recovery is a fresh, explicit invocation, never an automatic fallback/retry.
            await File.WriteAllTextAsync(fixture.Paths.ConfigPath, Yaml("显式重试目录"));
            var successful = new List<ProcessStartInfo>(); var retryErrors = new List<string>();
            var retry = Model(fixture.Client, fixture.Paths, editing, info => { successful.Add(info); return null; }, retryErrors);
            try
            {
                await retry.OpenLogsCommand.ExecuteAsync(null);
                AssertLaunch(Assert.Single(successful), Path.Combine(fixture.Root, "显式重试目录")); Assert.Empty(retryErrors);
                Assert.Equal(loaded.Revision, editing.Revision);
            }
            finally { retry.DetachDashboard(); }
        });
    }

    [Fact]
    public async Task OfflineLookupDoesNotStartAgentCreateDirectoriesOrOpenAStalePath()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-log-directory-offline-").FullName;
        var paths = new ProjectPaths(root); await File.WriteAllTextAsync(paths.ConfigPath, Yaml("offline-custom"));
        var before = Fingerprint(root); var launches = new List<ProcessStartInfo>(); var errors = new List<string>();
        try
        {
            await GuiConfigurationControlTests.RunStaAsync(async () =>
            {
                var client = new AgentClient(root); var editing = new AgentConfigurationClient(client);
                var model = Model(client, paths, editing, info => { launches.Add(info); return null; }, errors);
                try
                {
                    await model.OpenLogsCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
                    Assert.Empty(launches); Assert.Single(errors); Assert.StartsWith("日志目录未打开：", model.StatusMessage);
                    Assert.Null(editing.Revision); Assert.Equal(before, Fingerprint(root));
                    Assert.Equal([paths.ConfigPath], Directory.EnumerateFileSystemEntries(root).ToArray());
                    Assert.False(await client.PingAsync(TimeSpan.FromMilliseconds(150)));
                    await Evidence("directory-offline.json", new { root, errors, model.StatusMessage,
                        rootEntries = Directory.GetFileSystemEntries(root), noAgentAnswered = true, actualShellStarted = false });
                }
                finally { model.DetachDashboard(); }
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-log-directory-offline-", StringComparison.Ordinal)
                || new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException("Refusing non-owned offline fixture cleanup."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    [Fact]
    public async Task DetachedModelDoesNotOpenADirectory()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var attempts = 0; var errors = new List<string>();
            var model = Model(fixture.Client, fixture.Paths, new(fixture.Client), _ => { attempts++; return null; }, errors);
            var before = Fingerprint(fixture.Root); model.DetachDashboard();
            await model.OpenLogsCommand.ExecuteAsync(null);
            Assert.Equal(0, attempts); Assert.Empty(errors); Assert.Equal(before, Fingerprint(fixture.Root));
        });
    }

    private static MainWindowViewModel Model(AgentClient client, ProjectPaths paths, AgentConfigurationClient editing,
        Func<ProcessStartInfo, Process?> opener, List<string> errors)
    {
        var model = new MainWindowViewModel(client, new NativeCacheStore(paths.GuiCachePath), editing, paths,
            new NeverRemoteReader(), openLogDirectory: opener) { AutoRefresh = false };
        model.ErrorRequested += (_, message) => errors.Add(message); return model;
    }
    private static string Yaml(string? directory) => "# owned log-directory fixture\nversion: 1\nsettings:\n  default_startup_delay: 0\n"
        + (directory is null ? "" : $"  log_dir: '{directory.Replace("'", "''")}'\n") + "tasks: []\n";
    private static void AssertLaunch(ProcessStartInfo info, string directory)
    {
        Assert.Equal(directory, info.FileName); Assert.True(info.UseShellExecute);
        Assert.Empty(info.Arguments); Assert.Empty(info.ArgumentList); Assert.Empty(info.Verb);
    }
    private static string[] Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static async Task<(string Directory, string RootId, string Text)> ConsolePath(string root)
    {
        var executable = typeof(GuiLogDirectoryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in new[] { "logs", "path", "--root", root, "--json", "--non-interactive", "--timeout", "5" }) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode); Assert.Empty(await stderr); var text = await stdout;
            using var doc = JsonDocument.Parse(text); Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            return (doc.RootElement.GetProperty("data").GetProperty("logDirectory").GetString()!, doc.RootElement.GetProperty("rootId").GetString()!, text);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private static Task Evidence(string name, object data)
    {
        var directory = Environment.GetEnvironmentVariable("SA_LOG_GUI_EVIDENCE");
        return directory is null ? Task.CompletedTask : File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(data, AgentProtocol.JsonOptions));
    }
    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Directory tests never contact remote hosts.");
    }
}
