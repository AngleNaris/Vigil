using SystemAutorun.Core.Configuration;

namespace SystemAutorun.Core.Tests;

public sealed class ConfigurationSettingsSnapshotTests
{
    [Fact]
    public void SettingsProjectionNeedsNoRootOrOverrideFileAndDoesNotValidateTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), "SystemAutorun.SettingsSnapshot." + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(root));
        var settings = new ConfigurationLoader().ReadSettingsText("version: 1\nsettings: { backup_dir: archive, default_startup_delay: 2 }\ntasks: [{name: not-a-valid-task}]\n", root);
        Assert.Equal(Path.Combine(root, "archive"), settings.BackupDirectory); Assert.Equal(2, settings.DefaultStartupDelay);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("version: 3\nsettings: {}\ntasks: []")]
    [InlineData("version: 1\ntasks: []")]
    [InlineData("version: 1\nsettings: {}")]
    [InlineData("version: 1\nsettings: {default_startup_delay: -1}\ntasks: []")]
    [InlineData("version: 1\nsettings: {backup_dir: a, backup_dir: b}\ntasks: []")]
    [InlineData("version: 1\nsettings: [")]
    public void SettingsProjectionRetainsSharedSchemaAndYamlGuards(string yaml) =>
        Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().ReadSettingsText(yaml, Path.GetTempPath()));

    [Fact]
    public void SettingsProjectionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new ConfigurationLoader().ReadSettingsText("version: 1\nsettings: {}\ntasks: []", Path.GetTempPath(), cancellation.Token));
    }
}
