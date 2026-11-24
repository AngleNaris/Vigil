using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using Xunit;

namespace SystemAutorun.Core.Tests;

public sealed class AutoRestartSettingsTests
{
    [Fact]
    public void BuiltInPresets_AllValidateCleanly()
    {
        var settings = new AutoRestartSettings
        {
            Enabled = true,
            Rules = AutoRestartSettings.CreateBuiltInPresets(),
        }.CloneNormalized();

        AutoRestartSettingsValidator.Validate(settings);
        Assert.Equal(3, settings.Rules.Count);
        Assert.Contains(settings.Rules, r => r.Id == "dwm-compositor" && r.RequireElevation && r.ProcessName == "dwm");
        Assert.Contains(settings.Rules, r => r.Id == "explorer-factory-zombies" && r.CommandLineContains == "/factory");
        Assert.Contains(settings.Rules, r => r.Id == "dopus-handles" && r.HandleCountThreshold == 20000);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("system")]
    [InlineData("vigil.exe")]
    [InlineData("vigil.agent.exe")]
    public void Validator_RejectsSelfAndPseudoKernelProcesses(string processName)
    {
        var rule = new AutoRestartRule
        {
            Id = "bad-proc",
            Name = "Invalid",
            TargetKind = AutoRestartTargetKind.Process,
            ProcessName = processName,
            ActionMode = AutoRestartActionMode.TerminateOnly,
        };

        Assert.Throws<ArgumentException>(() => AutoRestartSettingsValidator.ValidateRule(rule));
    }

    [Fact]
    public void Validator_AllowsUserDefinedProcessesWithoutAllowlist()
    {
        var dwm = new AutoRestartRule
        {
            Id = "dwm",
            Name = "DWM",
            TargetKind = AutoRestartTargetKind.Process,
            ProcessName = "dwm.exe",
            ActionMode = AutoRestartActionMode.TerminateOnly,
            RequireElevation = true,
        }.Normalize();
        AutoRestartSettingsValidator.ValidateRule(dwm);

        // User has full freedom: unfiltered explorer, notepad, or arbitrary process with elevation & relaunch.
        var explorerAll = dwm with { Id = "exp", ProcessName = "explorer", CommandLineContains = null };
        AutoRestartSettingsValidator.ValidateRule(explorerAll);

        var notepadElevated = (dwm with
        {
            Id = "np",
            ProcessName = "notepad",
            ActionMode = AutoRestartActionMode.TerminateAndRelaunch,
            RequireElevation = true,
            InactiveHandleCountThreshold = 5000,
        }).Normalize();
        AutoRestartSettingsValidator.ValidateRule(notepadElevated);
        Assert.Equal(5000, notepadElevated.InactiveHandleCountThreshold);
    }

    [Theory]
    [InlineData("install-maintenance")]
    [InlineData("uninstall-maintenance")]
    [InlineData("run-maintenance")]
    public void ElevatedCommandPolicy_SupportsMaintenanceCommands(string command) =>
        Assert.True(ElevatedCommandPolicy.IsSupported(command));

    [Fact]
    public async Task ApplicationSettingsStore_RoundTripsAutoRestartSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SystemAutorun.CoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "autorun.gui.settings.json");
            var store = new ApplicationSettingsStore(path);
            var settings = new ApplicationSettings
            {
                AutoRestartSettings = new AutoRestartSettings
                {
                    Enabled = true,
                    Rules = AutoRestartSettings.CreateBuiltInPresets()
                        .Select((r, i) => i == 0 ? r with { Enabled = true } : r)
                        .ToArray(),
                },
            };

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert.True(loaded.AutoRestartSettings.Enabled);
            Assert.Equal(3, loaded.AutoRestartSettings.Rules.Count);
            Assert.True(loaded.AutoRestartSettings.Rules[0].Enabled);
            Assert.Equal("04:00", loaded.AutoRestartSettings.Rules[0].DailyTime);
            Assert.Equal(7.0, loaded.AutoRestartSettings.Rules[0].CpuPercentThreshold);
            Assert.Equal(20000, loaded.AutoRestartSettings.Rules[2].HandleCountThreshold);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
