using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentConfigurationClientTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task OneFrontendSerializesItsOwnRevisionCursor()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = new AgentConfigurationClient(fixture.Client);
        _ = await configuration.ReadAsync();
        var writes = Enumerable.Range(0, 40).Select(index => configuration.MutateAsync<ApplicationSettings>(
            "settings-patch", payload: new ApplicationSettingsPatch(AutoStartMissingOnLaunch: index % 2 != 0))).ToArray();
        WorkspaceMutation<ApplicationSettings>[] results;
        try { results = await Task.WhenAll(writes); }
        catch
        {
            foreach (var error in fixture.ObservedErrors)
            { output.WriteLine($"{error.Command} request={error.RequestId} operation={error.OperationId}: {error.Error}"); }
            throw;
        }
        var current = await configuration.ReadAsync();
        Assert.True(current.Preferences.AutoStartMissingOnLaunch);
        Assert.Equal(results[^1].Revision, current.Revision);
    }

    [Fact]
    public async Task ConflictInvalidatesCursorAndDoesNotSilentlyRebaseAnotherWrite()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var first = new AgentConfigurationClient(fixture.Client);
        var second = new AgentConfigurationClient(fixture.Client);
        _ = await first.ReadAsync();
        _ = await second.ReadAsync();
        _ = await first.MutateAsync<ApplicationSettings>("settings-patch",
            payload: new ApplicationSettingsPatch(AutoRecoverFailedTasks: true));
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() => second.MutateAsync<ApplicationSettings>(
            "settings-patch", payload: new ApplicationSettingsPatch(AutoStartMissingOnLaunch: true)));
        Assert.Equal("revision_conflict", conflict.Code);
        Assert.Null(second.Revision);
        var notLoaded = await Assert.ThrowsAsync<AgentCommandException>(() => second.MutateAsync<ApplicationSettings>(
            "settings-patch", payload: new ApplicationSettingsPatch(AutoStartMissingOnLaunch: true)));
        Assert.Equal("configuration_not_loaded", notLoaded.Code);
        var actual = await first.ReadAsync();
        Assert.True(actual.Preferences.AutoRecoverFailedTasks);
        Assert.False(actual.Preferences.AutoStartMissingOnLaunch);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task LegacyEnableDisableCannotBypassRevisionedWorkspace(string command)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = new AgentConfigurationClient(fixture.Client);
        _ = await configuration.ReadAsync();
        _ = await configuration.MutateAsync<ConfigurationMutationResult>("task-add", payload:
            new AddTaskRequest("fixture", AutorunTaskType.WslBoot, Distro: "never-run-fixture", HealthType: HealthCheckType.WslRunning));
        var before = await configuration.ReadAsync();
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<JsonElement>(command, "fixture"));
        Assert.Equal("unknown_command", error.Code);
        var after = await configuration.ReadAsync();
        Assert.Equal(before.Revision, after.Revision);
        Assert.True(after.Configuration.GetTask("fixture").Enabled);
    }
}
