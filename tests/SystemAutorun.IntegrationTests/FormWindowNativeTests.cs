using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;
using ThreadTask = System.Threading.Tasks.Task;

namespace SystemAutorun.IntegrationTests;

public sealed partial class DialogChromeTests
{
    [Fact]
    public async Task OwnedFormWindowsCanReturnAfterFailureAndNativeClose()
    {
        var probe = Environment.GetEnvironmentVariable("SA_FORM_MOTION_PROBE");
        if (probe is not null)
        {
            probe = Path.GetFullPath(probe);
            if (Path.GetDirectoryName(probe) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(probe).StartsWith("SystemAutorun-form-probe-", StringComparison.Ordinal)
                || !Directory.Exists(probe) || new DirectoryInfo(probe).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Expected an ordinary owned form probe directory.");
        }
        var root = Directory.CreateTempSubdirectory("SystemAutorun-form-motion-").FullName;
        try
        {
            var known = Path.Combine(root, "known_hosts"); const string contents = "# disabled fixture; no SSH connection\n";
            await File.WriteAllTextAsync(known, contents);
            var fixtureRemote = new RemoteHostDefinition
            {
                Id = "fixture", Name = "合成保留节点", Enabled = false, Platform = RemoteHostPlatform.Linux,
                Host = "192.0.2.10", UserName = "fixture", Authentication = RemoteAuthenticationMode.SshAgent,
                KnownHostsFile = known, ConnectTimeoutSeconds = 5, CommandTimeoutSeconds = 10,
            };
            await GuiConfigurationControlTests.RunStaAsync(async () =>
            {
                if (Application.Current is not SystemAutorun.App.App)
                { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
                WindowTheme.Install(); var owner = FormHidden(new Window { Title = "隔离工具回归 · 不写配置", Width = 960, Height = 620 });
                if (probe is not null) { owner.Left = 40; owner.Top = 40; }
                var calls = new Dictionary<string, int> { ["add"] = 0, ["remote"] = 0 };
                var observations = new List<object>();
                try
                {
                    owner.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var kinds = new[] { "add", "remote", "task", "message" };
                    if (probe is not null && Environment.GetEnvironmentVariable("SA_FORM_MOTION_PROBE_CASE") is { } only)
                    {
                        if (!kinds.Contains(only)) { throw new InvalidOperationException("Unknown form probe case."); }
                        kinds = [only];
                    }
                    foreach (var kind in kinds)
                    {
                        ToolWindow window = kind switch
                        {
                            "add" => new AddProgramWindow(StartupGuiControlPlaneTests.Entry, _ =>
                            { calls["add"]++; return ThreadTask.FromException<bool>(new InvalidOperationException("合成保存失败；未写配置，输入应保留。")); }),
                            "remote" => new RemoteHostsWindow([fixtureRemote], _ => { calls["remote"]++; return ThreadTask.FromException(new InvalidOperationException("合成保存失败；未写配置，草稿应保留。")); }, fixtureRemote),
                            _ => (ToolWindow)Create(kind),
                        };
                        FormHidden(window); window.Owner = owner;
                        if (probe is not null) { window.Left = 160; window.Top = 120; }
                        var interaction = window.Dispatcher.InvokeAsync(async () =>
                        {
                            try
                            {
                                await FormSettled(window);
                                if (probe is not null)
                                {
                                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"),
                                        $"Kind={kind}\nPID={Environment.ProcessId}\nHWND={new WindowInteropHelper(window).Handle}\nRoot={root}\nDPI={VisualTreeHelper.GetDpi(window).DpiScaleX}\nSynthetic callbacks; no persistence, SSH, startup or workloads.\n");
                                    await window.ClosureTask.WaitAsync(TimeSpan.FromMinutes(10));
                                }
                                else
                                {
                                    if (kind is "add" or "remote")
                                    {
                                        var dismiss = window.Dispatcher.InvokeAsync(async () =>
                                        {
                                            await PresenceMotionTests.Until(() => window.OwnedWindows.OfType<MessageDialog>().Any());
                                            var error = window.OwnedWindows.OfType<MessageDialog>().Single(); await FormSettled(error);
                                            FormClick(error.ConfirmButton); await error.ClosureTask;
                                        }).Task.Unwrap();
                                        FormClick(Descendants(window).OfType<Button>().Single(button => button.IsDefault)); await dismiss;
                                        await PresenceMotionTests.Until(() => window.IsEnabled);
                                    }
                                    SystemCommands.CloseWindow(window); await window.ClosureTask;
                                }
                            }
                            finally { FormClose(window); }
                        }).Task.Unwrap();
                        var result = window.ShowDialog(); await interaction;
                        Assert.False(result); Assert.True(window.ClosureTask.IsCompletedSuccessfully);
                        if (kind is "add" or "remote") { Assert.Equal(1, calls[kind]); }
                        var draft = window switch { AddProgramWindow add => add.NameTextBox.Text, RemoteHostsWindow remote => remote.NameTextBox.Text, _ => null };
                        if (kind is "add" or "remote") { Assert.False(string.IsNullOrWhiteSpace(draft)); }
                        observations.Add(new { Kind = kind, Result = result, Draft = draft, Calls = calls.GetValueOrDefault(kind), Closed = window.ClosureTask.IsCompletedSuccessfully });
                    }
                    Assert.False(owner.Dispatcher.HasShutdownStarted);
                    Assert.Equal(contents, await File.ReadAllTextAsync(known));
                    Assert.Single(Directory.GetFiles(root));
                    if (probe is not null) { await File.WriteAllTextAsync(Path.Combine(probe, "after.json"), JsonSerializer.Serialize(observations)); }
                }
                finally { owner.Close(); }
            }, timeout: probe is null ? null : TimeSpan.FromMinutes(45));
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-form-motion-", StringComparison.Ordinal)
                || new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Refusing non-owned fixture cleanup.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
