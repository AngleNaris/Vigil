using System.IO;
using System.Runtime.InteropServices;
using SystemAutorun.Windows.Scheduling;

namespace SystemAutorun.IntegrationTests;

public sealed class BootTaskSchedulerTests
{
    [Theory]
    [InlineData(0, BootTaskState.Unknown)]
    [InlineData(1, BootTaskState.Disabled)]
    [InlineData(2, BootTaskState.Queued)]
    [InlineData(3, BootTaskState.Ready)]
    [InlineData(4, BootTaskState.Running)]
    [InlineData(99, BootTaskState.Unknown)]
    public void MapsTaskSchedulerStates(int value, BootTaskState expected)
    {
        Assert.Equal(expected, BootTaskScheduler.MapState(value));
    }

    [Fact]
    public void QuotesBootAgentRootAsOneArgument()
    {
        Assert.Equal(
            "--boot --root \"C:\\Work Folder\\SystemAutorun\"",
            BootTaskScheduler.BuildAgentArguments(@"C:\Work Folder\SystemAutorun"));
    }

    [Fact]
    public void AcceptsDescendantPathWithinDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "SystemAutorun-boundary");

        Assert.True(BootConfigurationStore.IsWithinDirectory(Path.Combine(root, "logs"), root));
    }

    [Fact]
    public void RejectsEqualSiblingAndTraversedPaths()
    {
        var parent = Path.Combine(Path.GetTempPath(), "SystemAutorun-boundary-parent");
        var root = Path.Combine(parent, "Boot");

        Assert.False(BootConfigurationStore.IsWithinDirectory(root, root));
        Assert.False(BootConfigurationStore.IsWithinDirectory(Path.Combine(parent, "Boot-old"), root));
        Assert.False(BootConfigurationStore.IsWithinDirectory(Path.Combine(root, "..", "outside"), root));
    }

    [Fact]
    public void DeletesOnlyKnownStagingFiles()
    {
        var staging = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(staging, "autorun.yaml"), "settings: {}");
        File.WriteAllText(Path.Combine(staging, ".previous-autorun.yaml"), "settings: {}");

        BootConfigurationStore.DeleteKnownStagingDirectory(staging);

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void RefusesUnexpectedNestedStagingContent()
    {
        var staging = CreateTemporaryDirectory();
        var nested = Directory.CreateDirectory(Path.Combine(staging, "unexpected"));
        File.WriteAllText(Path.Combine(nested.FullName, "sentinel.txt"), "preserve");
        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => BootConfigurationStore.DeleteKnownStagingDirectory(staging));

            Assert.True(File.Exists(Path.Combine(nested.FullName, "sentinel.txt")));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Theory]
    [InlineData("lookup", unchecked((int)0x80070005))]
    [InlineData("lookup", unchecked((int)0x80041315))]
    [InlineData("delete", unchecked((int)0x80070005))]
    [InlineData("delete", unchecked((int)0x80041315))]
    [InlineData("verify", unchecked((int)0x80070005))]
    [InlineData("verify", unchecked((int)0x80041315))]
    public void UninstallDoesNotTurnPermissionOrSchedulerFailuresIntoAbsence(string stage, int code)
    {
        var failure = new COMException("Synthetic scheduler failure", code);
        var scheduler = new SchedulerDouble { FailureStage = stage, Failure = failure };

        Assert.Same(failure, Assert.Throws<COMException>(() => BootTaskScheduler.Uninstall(() => scheduler)));
        Assert.DoesNotContain("list", scheduler.Calls);
        Assert.DoesNotContain("folder-cleanup", scheduler.Calls);
    }

    [Theory]
    [InlineData("lookup", unchecked((int)0x80070002))]
    [InlineData("lookup", unchecked((int)0x80070003))]
    [InlineData("delete", unchecked((int)0x80070002))]
    [InlineData("delete", unchecked((int)0x80070003))]
    public void UninstallReturnsAlreadyAbsentOnlyForMissingFolderOrTask(string stage, int code)
    {
        var scheduler = new SchedulerDouble { FailureStage = stage, Failure = new COMException("Synthetic missing", code) };

        Assert.False(BootTaskScheduler.Uninstall(() => scheduler));
        Assert.Equal(stage == "lookup" ? new[] { "lookup" } : new[] { "lookup", "delete" }, scheduler.Calls);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002))]
    [InlineData(unchecked((int)0x80070003))]
    public void UninstallConfirmsTaskAbsenceBeforeOptionalFolderCleanup(int code)
    {
        var scheduler = new SchedulerDouble { FailureStage = "verify", Failure = new COMException("Synthetic missing", code) };

        Assert.True(BootTaskScheduler.Uninstall(() => scheduler));
        Assert.False(scheduler.TaskPresent);
        Assert.Equal(new[] { "lookup", "delete", "verify", "list", "root", "folder-cleanup" }, scheduler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UninstallFailsIfDeleteDidNotEstablishAbsence(bool nullTask)
    {
        var scheduler = new SchedulerDouble { IgnoreDelete = true, ReturnNullTask = nullTask };

        Assert.Throws<InvalidOperationException>(() => BootTaskScheduler.Uninstall(() => scheduler));
        Assert.True(scheduler.TaskPresent);
        Assert.Equal(new[] { "lookup", "delete", "verify" }, scheduler.Calls);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("folder-cleanup")]
    public void OptionalEmptyFolderCleanupFailureDoesNotUndoVerifiedTaskAbsence(string stage)
    {
        var scheduler = new SchedulerDouble { FailureStage = stage, Failure = new COMException("Synthetic cleanup denial", unchecked((int)0x80070005)) };

        Assert.True(BootTaskScheduler.Uninstall(() => scheduler));
        Assert.False(scheduler.TaskPresent);
        Assert.True(scheduler.Calls.IndexOf("verify") < scheduler.Calls.IndexOf(stage));
    }

    [Theory]
    [InlineData("lookup", false)]
    [InlineData("lookup", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("verify", false)]
    [InlineData("verify", true)]
    public void ClrMappedMissingErrorsHaveTheSameBoundedMeaning(string stage, bool directory)
    {
        Exception missing = directory ? new DirectoryNotFoundException("Synthetic missing") : new FileNotFoundException("Synthetic missing");
        var scheduler = new SchedulerDouble { FailureStage = stage, Failure = missing };

        Assert.Equal(stage == "verify", BootTaskScheduler.Uninstall(() => scheduler));
    }

    // Managed doubles only: no real scheduler, task, trigger or SYSTEM operation is created.
    public sealed class SchedulerDouble
    {
        public string? FailureStage { get; init; }
        public Exception? Failure { get; init; }
        public bool IgnoreDelete { get; init; }
        public bool ReturnNullTask { get; init; }
        public bool TaskPresent { get; set; } = true;
        public List<string> Calls { get; } = [];
        public void Visit(string stage)
        {
            Calls.Add(stage);
            if (FailureStage == stage) { throw Failure!; }
        }
        public object GetFolder(string path)
        {
            if (path == @"\") { Visit("root"); return new RootDouble(this); }
            Assert.Equal(BootTaskScheduler.FolderPath, path); Visit("lookup"); return new FolderDouble(this);
        }
    }
    public sealed class FolderDouble(SchedulerDouble owner)
    {
        public void DeleteTask(string name, int flags)
        {
            Assert.Equal(BootTaskScheduler.TaskName, name); Assert.Equal(0, flags); owner.Visit("delete");
            if (!owner.IgnoreDelete) { owner.TaskPresent = false; }
        }
        public object? GetTask(string name)
        {
            Assert.Equal(BootTaskScheduler.TaskName, name); owner.Visit("verify");
            if (!owner.TaskPresent) { throw new COMException("Synthetic absent task", unchecked((int)0x80070002)); }
            return owner.ReturnNullTask ? null : new object();
        }
        public TasksDouble GetTasks(int flags) { Assert.Equal(1, flags); owner.Visit("list"); return new(); }
    }
    public sealed class TasksDouble { public int Count => 0; }
    public sealed class RootDouble(SchedulerDouble owner)
    {
        public void DeleteFolder(string path, int flags)
        { Assert.Equal(BootTaskScheduler.FolderPath, path); Assert.Equal(0, flags); owner.Visit("folder-cleanup"); }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SystemAutorun.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
