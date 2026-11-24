using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.Agent;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Cli;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class AutoRestartFeatureTests
{
    [Fact]
    public void ScheduleComputation_HandlesIntervalAndDailyWithoutImmediateFiring()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("Test+8", TimeSpan.FromHours(8), "Test+8", "Test+8");
        var armedAt = new DateTimeOffset(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        var now = armedAt.AddMinutes(5);

        // Daily at 04:00 armed at 20:00 should schedule for next day 04:00, NOT immediately.
        var dailyRule = new AutoRestartRule
        {
            Id = "dwm",
            Name = "DWM",
            Enabled = true,
            ScheduleKind = AutoRestartScheduleKind.Daily,
            DailyTime = "04:00",
        };
        var nextDaily = AgentAutoRestartWorker.ComputeNextScheduledAt(dailyRule, now, armedAt, null, null, tz);
        Assert.NotNull(nextDaily);
        var nextDailyLocal = TimeZoneInfo.ConvertTime(nextDaily!.Value, tz);
        Assert.Equal(new DateTime(2026, 11, 25, 4, 0, 0), nextDailyLocal.DateTime);
        Assert.True(now < nextDaily.Value);

        // At 04:01 next day, it becomes due; after recording "2026-11-25", next shifts to 2026-11-26.
        var nextMorning = new DateTimeOffset(2026, 11, 25, 4, 1, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        var dueAt = AgentAutoRestartWorker.ComputeNextScheduledAt(dailyRule, nextMorning, armedAt, null, null, tz);
        Assert.True(nextMorning >= dueAt!.Value);

        var afterRun = AgentAutoRestartWorker.ComputeNextScheduledAt(dailyRule, nextMorning, armedAt, nextMorning, "2026-11-25", tz);
        Assert.Equal(new DateTime(2026, 11, 26, 4, 0, 0), TimeZoneInfo.ConvertTime(afterRun!.Value, tz).DateTime);

        // Interval 60 minutes.
        var intervalRule = new AutoRestartRule
        {
            Id = "exp",
            Name = "Explorer",
            Enabled = true,
            ScheduleKind = AutoRestartScheduleKind.Interval,
            IntervalMinutes = 60,
        };
        var nextInterval = AgentAutoRestartWorker.ComputeNextScheduledAt(intervalRule, now, armedAt, null, null, tz);
        Assert.Equal(armedAt.AddMinutes(60), nextInterval);
    }

    [Fact]
    public async Task ProcessMaintenanceExecutor_MatchesCommandLineReadsHandlesAndTerminatesTargetOnly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SystemAutorun.AutoRestartTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "autorun.yaml"), "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
        var marker = $"VIGIL_AR_MARKER_{Guid.NewGuid():N}";
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/c \"title {marker} && ping -n 30 127.0.0.1 >nul\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(child);
            await Task.Delay(300);
            Assert.False(child!.HasExited, "Test marker process exited before matching.");

            var executor = new ProcessMaintenanceExecutor();
            var rule = new AutoRestartRule
            {
                Id = "test-marker-cmd",
                Name = "Test Marker Cmd",
                Enabled = true,
                TargetKind = AutoRestartTargetKind.Process,
                ProcessName = "cmd.exe",
                CommandLineContains = marker,
                ActionMode = AutoRestartActionMode.TerminateOnly,
            }.Normalize();

            var all = executor.ReadAllProcessMetrics();
            var matches = executor.MatchProcesses(rule, all);
            Assert.Single(matches);
            Assert.Equal(child!.Id, matches[0].ProcessId);
            Assert.True(matches[0].HandleCount > 0, "NativeProcessMetric should expose non-zero HandleCount.");

            var run = await executor.ExecuteProcessRuleAsync(new ProjectPaths(tempRoot), rule);
            Assert.True(run.Succeeded, run.Message);
            Assert.Equal(1, run.AffectedProcessCount);
            Assert.True(child.WaitForExit(3000), "Matched process should have been terminated.");
        }
        finally
        {
            try { if (child is { HasExited: false }) { child.Kill(entireProcessTree: true); } } catch { }
            child?.Dispose();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CliArguments_ParsesAutoRestartCommandsAndRequiresConfirmationOnRun()
    {
        var status = CliArguments.Parse(["autorestart", "status", "--json"]);
        Assert.Equal("auto-restart-status", status.Route.Rpc);
        Assert.True(status.Json);

        Assert.Throws<CliAuthorizationException>(() => CliArguments.Parse(["autorestart", "run", "dwm-compositor"]));

        var run = CliArguments.Parse(["autorestart", "run", "dwm-compositor", "--yes", "--json"]);
        Assert.Equal("auto-restart-run", run.Route.Rpc);
        Assert.Equal("dwm-compositor", run.Name);
    }

    [Fact]
    public Task AutomationTab_RendersPresetsEditsSavesAndRuns() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (_, main, _) =>
        {
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

            var initialSettings = new AutoRestartSettings
            {
                Enabled = true,
                Rules = AutoRestartSettings.CreateBuiltInPresets()
                    .Select((r, idx) => idx == 0
                        ? r with { Enabled = true, MemoryMbThreshold = 1024, HandleCountThreshold = 20000, InactiveHandleCountThreshold = 5000 }
                        : r)
                    .ToArray(),
            };

            var now = DateTimeOffset.UtcNow;
            var dwmSamples = Enumerable.Range(0, 30).Select(i =>
            {
                var at = now.AddSeconds(-(29 - i) * 10);
                var cpu = 1.4 + Math.Sin(i * 0.45) * 0.9 + (i > 22 ? (i - 22) * 0.75 : 0);
                var mem = (280L + i * 3L) * 1024L * 1024L;
                var handles = 1380 + i * 8;
                var inactive = Math.Max(0, i * 6 - 10);
                return new AutoRestartMetricSample(at, Math.Round(cpu, 1), mem, handles, inactive, 1, cpu >= 7.0);
            }).ToArray();

            var dopusSamples = Enumerable.Range(0, 30).Select(i =>
            {
                var at = now.AddSeconds(-(29 - i) * 10);
                var cpu = i % 9 == 0 ? 0.2 : 0.0;
                var mem = (145L + (i % 5) * 4L) * 1024L * 1024L;
                var handles = 13400 + i * 35;
                var inactive = handles;
                return new AutoRestartMetricSample(at, cpu, mem, handles, inactive, 1, false);
            }).ToArray();

            var sampleStatus = new AutoRestartStatusSnapshot(
                Enabled: true,
                IsAgentElevated: true,
                MaintenanceTaskInstalled: false,
                MaintenanceTaskError: null,
                CapturedAt: now,
                Rules:
                [
                    new AutoRestartRuleStatus(
                        "dwm-compositor",
                        "桌面合成器 (dwm.exe)",
                        true,
                        1,
                        5.8,
                        368L * 1024 * 1024,
                        1612,
                        null,
                        now.AddHours(-6),
                        "定时触发（每日 04:00）",
                        true,
                        "已通过系统维护通道热重启 1 个 dwm 进程。",
                        now.AddHours(8),
                        null,
                        PeakCpuPercent: 7.4,
                        PeakWorkingSetBytes: 412L * 1024 * 1024,
                        PeakHandleCount: 1640,
                        LiveInactiveHandleCount: 164,
                        PeakInactiveHandleCount: 210,
                        LiveThreadCount: 24,
                        LiveGdiHandleCount: 84,
                        LiveUserHandleCount: 42,
                        Samples: dwmSamples),
                    new AutoRestartRuleStatus(
                        "explorer-factory-zombies",
                        "僵尸资源管理器 (explorer /factory)",
                        false,
                        0,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                    new AutoRestartRuleStatus(
                        "dopus-handles",
                        "Directory Opus (dopus.exe)",
                        false,
                        1,
                        0.0,
                        156L * 1024 * 1024,
                        14415,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        PeakCpuPercent: 1.9,
                        PeakWorkingSetBytes: 248L * 1024 * 1024,
                        PeakHandleCount: 14980,
                        LiveInactiveHandleCount: 14415,
                        PeakInactiveHandleCount: 14980,
                        LiveThreadCount: 38,
                        LiveGdiHandleCount: 312,
                        LiveUserHandleCount: 188,
                        Samples: dopusSamples),
                ]);

            AutoRestartSettings? saved = null;
            string? executedRuleId = null;

            // Verify peer tab order: 服务 -> 自动化 -> 主机 -> 异常日志
            var tabNames = main.ViewTabs.Children.OfType<System.Windows.Controls.Button>().Select(b => b.Name).ToArray();
            Assert.Equal(["ServicesTab", "AutomationTab", "HostsTab", "ExceptionLogsTab"], tabNames);
            Assert.Equal("自动化", main.AutomationTab.Content);

            var view = main.AutomationViewControl;
            view.LoadDirectForTest(
                initialSettings,
                tasks,
                sampleStatus,
                draft =>
                {
                    saved = draft;
                    return Task.CompletedTask;
                },
                id =>
                {
                    executedRuleId = id;
                    return Task.FromResult(new AutoRestartRunResult(id, true, "测试执行成功", 1, false, DateTimeOffset.UtcNow));
                },
                _ => Task.FromResult(true));

            // Switch to Automation peer tab
            ColorStateAssist.SetReduceMotion(main, true);
            main.SelectTab(SystemAutorun.App.ViewModels.MainWindowViewModel.AutomationPage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            Assert.True(main.ViewModel.IsAutomationPage);
            Assert.True(main.AutomationPage.IsPresented);
            Assert.Equal(3, view.RuleList.Items.Count);
            Assert.Equal(Visibility.Visible, view.RuleDashboardGrid.Visibility);
            Assert.Contains("自动：已启用", view.AutoToggleText.Text);
            Assert.Contains("峰值 7.4%", view.CpuPeakText.Text);
            Assert.Contains("峰值 412 MB", view.MemoryPeakText.Text);
            SaveScreenshotIfRequested(main, "automation-dashboard-dwm-dark.png");

            // Select Directory Opus preset (shows huge inactive handle telemetry!)
            view.RuleList.SelectedIndex = 2;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            Assert.Contains("自动：已停用", view.AutoToggleText.Text);
            Assert.Contains("14,415", view.InactiveHandlesReadout.Text);
            SaveScreenshotIfRequested(main, "automation-dashboard-dopus-dark.png");

            // RunOnce executes WITHOUT changing the auto-trigger toggle
            view.RunOnceButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("dopus-handles", executedRuleId);
            Assert.Null(saved);

            // AutoToggle explicitly toggles auto-triggering and persists
            view.AutoToggleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.NotNull(saved);
            Assert.True(saved!.Enabled);
            Assert.True(saved.Rules.Single(r => r.Id == "dopus-handles").Enabled);

            // Verify rule editor modal dialog renders cleanly & accepts arbitrary user process + inactive handle threshold
            var editor = new AutoRestartRuleWindow(saved.Rules.Single(r => r.Id == "dopus-handles"), tasks, isNew: false) { Owner = main };
            try
            {
                ColorStateAssist.SetReduceMotion(editor, true);
                editor.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                editor.UpdateLayout();
                SaveScreenshotIfRequested(editor, "automation-rule-dialog-dark.png");
                editor.ProcessNameTextBox.Text = "obs64";
                editor.RequireElevationCheckBox.IsChecked = true;
                editor.InactiveHandleThresholdTextBox.Text = "12000";
                editor.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.NotNull(editor.ResultRule);
                Assert.Equal("obs64", editor.ResultRule!.ProcessName);
                Assert.True(editor.ResultRule.RequireElevation);
                Assert.Equal(12000, editor.ResultRule.InactiveHandleCountThreshold);
            }
            finally
            {
                if (editor.IsVisible) { editor.Close(); }
            }

            // Also verify Light theme rendering
            var originalTheme = ThemeService.Preference;
            try
            {
                ThemeService.Set(ThemePreference.Light);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SaveScreenshotIfRequested(main, "automation-dashboard-dopus-light.png");
            }
            finally
            {
                ThemeService.Set(originalTheme);
            }
        });

    private static void SaveScreenshotIfRequested(FrameworkElement visual, string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("SA_AUTORESTART_EVIDENCE");
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
