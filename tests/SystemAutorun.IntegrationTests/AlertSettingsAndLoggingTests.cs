using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;
using MainWindow = SystemAutorun.App.MainWindow;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class AlertSettingsAndLoggingTests
{
    [Fact]
    public async Task ResourceAlertSettings_PersistValidateAndResolvePerTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SystemAutorun.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "gui.settings.json");
            var store = new ApplicationSettingsStore(path);
            var settings = new ApplicationSettings
            {
                AutoStartMissingOnLaunch = true,
                AlertSettings = new ResourceAlertSettings
                {
                    Enabled = true,
                    CpuSustainSeconds = 45,
                    HostCpuPercent = 82,
                    HostMemoryPercent = 88,
                    HostStoragePercent = 91,
                    ProcessCpuPercent = 75,
                    ProcessMemoryPercent = 30,
                    ProcessMemoryMb = 4096,
                    HostOverrides = new Dictionary<string, HostAlertOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["local"] = new HostAlertOverride
                        {
                            CpuPercent = 92,
                            StoragePercent = 96,
                            CpuSustainSeconds = 30,
                        },
                        ["nas-01"] = new HostAlertOverride
                        {
                            Enabled = false,
                        },
                    },
                    TaskOverrides = new Dictionary<string, TaskAlertOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["WSL Ubuntu-24.04"] = new TaskAlertOverride
                        {
                            CpuPercent = 65,
                            MemoryPercent = 50,
                            MemoryMb = 0,
                        },
                    },
                },
            };

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert.True(loaded.AutoStartMissingOnLaunch);
            Assert.Equal(45, loaded.AlertSettings.CpuSustainSeconds);

            var local = loaded.AlertSettings.ResolveHost("LOCAL");
            Assert.True(local.Enabled);
            Assert.Equal(92, local.CpuPercent);
            Assert.Equal(87, local.CpuResetPercent);
            Assert.Equal(88, local.MemoryPercent);
            Assert.Equal(96, local.StoragePercent);
            Assert.Equal(TimeSpan.FromSeconds(30), local.CpuSustainDuration);

            var nas = loaded.AlertSettings.ResolveHost("nas-01");
            Assert.False(nas.Enabled);

            var wsl = loaded.AlertSettings.ResolveTask("wsl ubuntu-24.04");
            Assert.True(wsl.Enabled);
            Assert.Equal(65, wsl.CpuPercent);
            Assert.Equal(50, wsl.MemoryPercent);
            Assert.Equal(0, wsl.MemoryBytes);

            await Assert.ThrowsAsync<ConfigurationException>(() => store.SaveAsync(settings with
            {
                AlertSettings = settings.AlertSettings with { HostCpuPercent = 120 },
            }));
            await Assert.ThrowsAsync<ConfigurationException>(() => store.SaveAsync(settings with
            {
                AlertSettings = settings.AlertSettings with { CpuSustainSeconds = -1 },
            }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResourceAndFailureAlerts_WriteToLogsAndAppearInExceptionLogs()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var cursor = new AgentConfigurationClient(fixture.Client);
        _ = await cursor.ReadAsync();
        var executable = Path.Combine(fixture.Root, "worker-fixture.exe");
        await File.WriteAllTextAsync(executable, "not executable");
        _ = await cursor.MutateAsync<ConfigurationMutationResult>("task-add", payload: new AddTaskRequest(
            "wsl-worker",
            AutorunTaskType.Windows,
            Executable: executable,
            HealthType: HealthCheckType.Process,
            HealthPattern: "worker-fixture.exe",
            ExpectedRunning: true));

        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var adapter = new DashboardFixtureMonitoringClient(fixture);
            var model = new MainWindowViewModel(
                fixture.Client,
                new NativeCacheStore(fixture.Paths.GuiCachePath),
                cursor,
                fixture.Paths,
                new NeverRemoteReader(),
                adapter);
            model.InitializeDashboard();
            try
            {
                // Pre-configure custom thresholds cleanly via ApplyAlertSettingsAsync.
                var original = await model.PrepareAlertSettingsEditAsync();
                await model.ApplyAlertSettingsAsync(new ResourceAlertSettings
                {
                    Enabled = true,
                    CpuSustainSeconds = 0,
                    HostCpuPercent = 90,
                    HostMemoryPercent = 99,
                    HostStoragePercent = 90,
                    ProcessCpuPercent = 70,
                    ProcessMemoryPercent = 20,
                    ProcessMemoryMb = 0,
                    HostOverrides = new Dictionary<string, HostAlertOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["local"] = new HostAlertOverride
                        {
                            CpuPercent = 99,
                            MemoryPercent = 100,
                            StoragePercent = 90,
                            CpuSustainSeconds = 60,
                        },
                    },
                    TaskOverrides = new Dictionary<string, TaskAlertOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["wsl-worker"] = new TaskAlertOverride
                        {
                            CpuPercent = 70,
                            MemoryPercent = 50,
                            MemoryMb = 0,
                            CpuSustainSeconds = 0,
                        },
                    },
                }, original);

                await model.InitializeAsync();
                var alerts = new List<string>();
                model.AlertRequested += (_, message) => alerts.Add(message);

                // Trigger high CPU on wsl-worker (76% >= 70% threshold, CpuSustainSeconds = 0) and high disk on local (95% >= 90%).
                var configuration = await cursor.ReadAsync();
                var baseSnapshot = adapter.Snapshot(configuration);
                var at = DateTimeOffset.UtcNow;
                var taskSnap = baseSnapshot.Tasks.Data![0];
                var procSnap = new ManagedProcessSnapshot(
                    taskSnap.Name, "fixture", taskSnap.Type, true, true, true, "OK", "fixture", "fixture", at,
                    [new(4242, "worker-fixture.exe", null, at.AddHours(-1), 76.2, 50, 50, 1, true)],
                    TaskIdentity: MonitoringTaskIdentity.For(taskSnap));
                var hostSnap = new HostStatusSnapshot(
                    Environment.MachineName,
                    "synthetic host",
                    at,
                    TimeSpan.FromDays(1),
                    20,
                    1000,
                    500, // 50% RAM < 100% threshold
                    [new("C:\\", "System", 1000, 50)], // 95% used >= 90% storage threshold
                    []);

                adapter.SnapshotReader = _ => Task.FromResult(baseSnapshot with
                {
                    ReturnedAt = at,
                    Tasks = baseSnapshot.Tasks with
                    {
                        ObservedAt = at,
                        AttemptedAt = at,
                        Data = [taskSnap with { Status = "FAIL", Healthy = false, HealthObserved = true, Reason = "process-not-found" }],
                    },
                    Processes = baseSnapshot.Processes with
                    {
                        ObservedAt = at,
                        AttemptedAt = at,
                        Data = [procSnap],
                    },
                    Host = baseSnapshot.Host with
                    {
                        ObservedAt = at,
                        AttemptedAt = at,
                        Data = hostSnap,
                    },
                });

                await model.RefreshCommand.ExecuteAsync(null);
                await model.FlushAlertLogsAsync();

                var row = Assert.Single(model.Tasks);
                Assert.True(row.HasHighResourceUsage);
                Assert.Contains("76.2%", row.HighResourceSummary);

                var localTab = model.Hosts.First(host => host.IsLocal);
                Assert.True(localTab.HasHighResourceUsage);
                Assert.Contains("磁盘高占用", localTab.HighResourceSummary);

                // Refresh once more with slight CPU jitter (78.4%) — should NOT emit duplicate alerts.
                var countBeforeJitter = alerts.Count;
                var at2 = at.AddSeconds(2);
                adapter.SnapshotReader = _ => Task.FromResult(baseSnapshot with
                {
                    ReturnedAt = at2,
                    Tasks = baseSnapshot.Tasks with
                    {
                        ObservedAt = at2,
                        AttemptedAt = at2,
                        Data = [taskSnap with { Status = "FAIL", Healthy = false, HealthObserved = true, Reason = "process-not-found" }],
                    },
                    Processes = baseSnapshot.Processes with
                    {
                        ObservedAt = at2,
                        AttemptedAt = at2,
                        Data = [procSnap with { CapturedAt = at2, Processes = [procSnap.Processes[0] with { CpuUsagePercent = 78.4 }] }],
                    },
                    Host = baseSnapshot.Host with
                    {
                        ObservedAt = at2,
                        AttemptedAt = at2,
                        Data = hostSnap with { CapturedAt = at2 },
                    },
                });

                await model.RefreshCommand.ExecuteAsync(null);
                await model.FlushAlertLogsAsync();
                Assert.Equal(countBeforeJitter, alerts.Count);

                // Simulate a flapping service (OK -> FAIL again within default 10m cooldown): must NOT spam repeat failure alerts.
                var at3 = at2.AddSeconds(5);
                adapter.SnapshotReader = _ => Task.FromResult(baseSnapshot with
                {
                    ReturnedAt = at3,
                    Tasks = baseSnapshot.Tasks with
                    {
                        ObservedAt = at3,
                        AttemptedAt = at3,
                        Data = [taskSnap with { Status = "OK", Healthy = true, HealthObserved = true, Reason = "running" }],
                    },
                    Processes = baseSnapshot.Processes with { ObservedAt = at3, AttemptedAt = at3, Data = [procSnap] },
                    Host = baseSnapshot.Host with { ObservedAt = at3, AttemptedAt = at3, Data = hostSnap with { CapturedAt = at3 } },
                });
                await model.RefreshCommand.ExecuteAsync(null);

                var at4 = at3.AddSeconds(5);
                adapter.SnapshotReader = _ => Task.FromResult(baseSnapshot with
                {
                    ReturnedAt = at4,
                    Tasks = baseSnapshot.Tasks with
                    {
                        ObservedAt = at4,
                        AttemptedAt = at4,
                        Data = [taskSnap with { Status = "FAIL", Healthy = false, HealthObserved = true, Reason = "process-not-found" }],
                    },
                    Processes = baseSnapshot.Processes with { ObservedAt = at4, AttemptedAt = at4, Data = [procSnap] },
                    Host = baseSnapshot.Host with { ObservedAt = at4, AttemptedAt = at4, Data = hostSnap with { CapturedAt = at4 } },
                });
                await model.RefreshCommand.ExecuteAsync(null);
                await model.FlushAlertLogsAsync();
                Assert.Equal(countBeforeJitter, alerts.Count);

                Assert.Contains(model.ExceptionLogs, entry => entry.Message.Contains("[资源告警]", StringComparison.Ordinal)
                    && entry.Message.Contains("wsl-worker", StringComparison.Ordinal));
                Assert.Contains(model.ExceptionLogs, entry => entry.Message.Contains("[状态告警]", StringComparison.Ordinal)
                    && entry.Message.Contains("wsl-worker", StringComparison.Ordinal));
                Assert.Contains(model.ExceptionLogs, entry => entry.Message.Contains("[资源告警]", StringComparison.Ordinal)
                    && entry.Message.Contains("磁盘高占用", StringComparison.Ordinal));
            }
            finally
            {
                model.DetachDashboard();
                await model.DashboardHistoryTask;
                await model.ResourceHistoryTask;
            }
        });
    }

    [Fact]
    public Task BootTaskMenu_HidesInstallWhenInstalledAndShowsUninstall() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (_, main, __) =>
        {
            var submenuMethod = typeof(MainWindow).GetMethod(
                "WindowsStartupMenu_SubmenuOpened",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            main.BootTaskInstalledProbe = () => false;
            submenuMethod.Invoke(main, [main.WindowsStartupMenu, new RoutedEventArgs()]);
            Assert.Equal(Visibility.Visible, main.InstallBootMenuItem.Visibility);
            Assert.Equal(Visibility.Collapsed, main.UninstallBootMenuItem.Visibility);

            main.BootTaskInstalledProbe = () => true;
            submenuMethod.Invoke(main, [main.WindowsStartupMenu, new RoutedEventArgs()]);
            Assert.Equal(Visibility.Collapsed, main.InstallBootMenuItem.Visibility);
            Assert.Equal(Visibility.Visible, main.UninstallBootMenuItem.Visibility);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        });

    [Fact]
    public Task HostContextMenu_AllowsLocalHostAlertSettingsWhileHidingRemoteEditAndRemove() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, _) =>
        {
            model.CurrentPage = MainWindowViewModel.OverviewPage;
            main.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var localHost = model.Hosts.First(host => host.IsLocal);
            model.SelectedHostTab = localHost;
            var view = main.MonitoringTabs;
            var container = view.HostList.ItemContainerGenerator.ContainerFromItem(localHost) as DependencyObject ?? view.HostList;
            var openingMethod = typeof(MonitoringView).GetMethod(
                "HostList_ContextMenuOpening",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var localArgs = CreateContextMenuArgs(container);
            openingMethod.Invoke(view, [view.HostList, localArgs]);
            Assert.False(localArgs.Handled);
            Assert.Equal(Visibility.Visible, view.HostAlertSettingsMenuItem.Visibility);
            Assert.Equal(Visibility.Collapsed, view.EditHostMenuItem.Visibility);
            Assert.Equal(Visibility.Collapsed, view.RemoveHostMenuItem.Visibility);
        });

    [Fact]
    public Task AlertSettingsWindow_RendersTargetsAndSavesOverrides() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (_, main, _) =>
        {
            var initial = new ResourceAlertSettings
            {
                HostCpuPercent = 85,
                HostMemoryPercent = 85,
                HostStoragePercent = 90,
                ProcessCpuPercent = 85,
                ProcessMemoryPercent = 25,
                ProcessMemoryMb = 2048,
                CpuSustainSeconds = 60,
            };
            RemoteHostDefinition[] hosts =
            [
                new RemoteHostDefinition
                {
                    Id = "home-nas",
                    Name = "Home NAS",
                    Enabled = true,
                    Platform = RemoteHostPlatform.Linux,
                    Host = "192.168.1.10",
                    Port = 22,
                    UserName = "ops",
                    Authentication = RemoteAuthenticationMode.SshAgent,
                    KnownHostsFile = @"C:\Keys\known_hosts",
                },
            ];
            AutorunTask[] tasks =
            [
                new AutorunTask(
                    "WSL Ubuntu-24.04",
                    AutorunTaskType.WslBoot,
                    true,
                    "",
                    null,
                    [],
                    null,
                    "Ubuntu-24.04",
                    null,
                    null,
                    [],
                    0,
                    new HealthCheckDefinition(HealthCheckType.WslRunning, null, null, null, 30, "Ubuntu-24.04", null),
                    LaunchOptions.LegacyDefault(AutorunTaskType.WslBoot),
                    false),
            ];

            ResourceAlertSettings? saved = null;
            var dialog = new AlertSettingsWindow(
                initial,
                hosts,
                tasks,
                draft =>
                {
                    saved = draft;
                    return Task.CompletedTask;
                },
                focusTargetKey: "task:WSL Ubuntu-24.04");
            try
            {
                ColorStateAssist.SetReduceMotion(dialog, true);
                dialog.Owner = main;
                dialog.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                dialog.UpdateLayout();

                Assert.Equal(4, dialog.TargetList.Items.Count);
                Assert.Equal(Visibility.Visible, dialog.TaskOverridePanel.Visibility);
                Assert.Equal(Visibility.Collapsed, dialog.GlobalEditorPanel.Visibility);

                dialog.TaskCustomCheckBox.IsChecked = true;
                dialog.TaskCustomCheckBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                dialog.TaskCpuTextBox.Text = "65";
                dialog.TaskMemMbTextBox.Text = "0";
                dialog.TaskSustainTextBox.Text = "15";
                dialog.UpdateLayout();

                SaveDialogScreenshotIfRequested(dialog, "alert-settings-task.png");

                // Switch to global defaults and tweak storage threshold.
                dialog.TargetList.SelectedIndex = 0;
                dialog.UpdateLayout();
                Assert.Equal(Visibility.Visible, dialog.GlobalEditorPanel.Visibility);
                dialog.GlobalHostStorageTextBox.Text = "92";
                dialog.UpdateLayout();

                SaveDialogScreenshotIfRequested(dialog, "alert-settings-global.png");

                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.NotNull(saved);
                Assert.Equal(92, saved!.HostStoragePercent);
                var taskRule = saved.FindTaskOverride("WSL Ubuntu-24.04");
                Assert.NotNull(taskRule);
                Assert.Equal(65, taskRule!.CpuPercent);
                Assert.Equal(0, taskRule.MemoryMb);
                Assert.Equal(15, taskRule.CpuSustainSeconds);
            }
            finally
            {
                if (dialog.IsVisible) { dialog.Close(); }
            }
        });

    private static ContextMenuEventArgs CreateContextMenuArgs(DependencyObject originalSource)
    {
        var args = (ContextMenuEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ContextMenuEventArgs));
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        args.Source = originalSource;
        return args;
    }

    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"Unexpected remote read for {host.Id}.");
    }

    private static void SaveDialogScreenshotIfRequested(FrameworkElement visual, string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("SA_ALERT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(dir)) { return; }
        Directory.CreateDirectory(dir);
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0) { return; }
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(dir, fileName));
        encoder.Save(stream);
    }
}
