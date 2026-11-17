using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed class StartupRegistrationReaderTests
{
    [Theory]
    [InlineData(RegistryValueKind.String)] [InlineData(RegistryValueKind.ExpandString)]
    public void OwnedRegistryValueIsReadWithoutExpansionModificationOrTargetProbing(RegistryValueKind kind)
    {
        WithOwnedKey(key =>
        {
            const string command = "\"\\\\offline.invalid\\%UNEXPANDED%\\Vigil.exe\" --background --root \"C:\\owned\"";
            key.SetValue("Fixture", command, kind);
            var data = StartupRegistrationReader.ReadSelfValue(key, "Fixture");
            Assert.True(data.Registered); Assert.Null(data.Enabled); Assert.Equal(command, data.Command);
            Assert.Equal(kind.ToString(), data.ValueKind);
            Assert.Equal(command, key.GetValue("Fixture", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(kind, key.GetValueKind("Fixture"));
        });
    }

    [Fact]
    public void MissingAndNonTextRegistryValuesDoNotInventACommand()
    {
        WithOwnedKey(key =>
        {
            Assert.False(StartupRegistrationReader.ReadSelfValue(key, "Fixture").Registered);
            key.SetValue("Fixture", 42, RegistryValueKind.DWord);
            var data = StartupRegistrationReader.ReadSelfValue(key, "Fixture");
            Assert.True(data.Registered); Assert.Equal("DWord", data.ValueKind); Assert.Null(data.Command);
            Assert.Equal(42, key.GetValue("Fixture"));
        });
    }

    [Fact]
    public void OversizedRegistryValueFailsWithNoGrowingAllocationOrTruncation()
    {
        WithOwnedKey(key =>
        {
            key.SetValue("Fixture", new string('x', 8193), RegistryValueKind.String);
            Assert.Equal("registration_too_large", Assert.Throws<StartupRegistrationSourceException>(() =>
                StartupRegistrationReader.ReadSelfValue(key, "Fixture")).Reason);
            Assert.Equal(8193, ((string)key.GetValue("Fixture")!).Length);
        });
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002), true)] [InlineData(unchecked((int)0x80070003), true)]
    [InlineData(unchecked((int)0x80070005), false)] [InlineData(unchecked((int)0x80041315), false)]
    public void OnlyMissingFolderOrTaskMeansUnregistered(int code, bool absent)
    {
        var service = new FakeScheduler { Failure = code };
        if (absent) { Assert.False(StartupRegistrationReader.ReadBoot(() => service).Registered); }
        else { Assert.Equal(code, Assert.Throws<COMException>(() => StartupRegistrationReader.ReadBoot(() => service)).HResult); }
        Assert.Equal(new[] { "Connect", "GetFolder" }, service.Calls);
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(1, 5)] [InlineData(2, 0)]
    public void BootReadsOnlyFixedMetadataAndNeverInvokesAnAction(int count, int type)
    {
        var service = new FakeScheduler(); service.Task.Definition.Actions.Count = count; service.Task.Definition.Actions.Action.Type = type;
        var data = StartupRegistrationReader.ReadBoot(() => service);
        Assert.True(data.Registered); Assert.False(data.Enabled); Assert.Equal("ready", data.State);
        Assert.Equal(count, data.ActionCount); Assert.Equal(7, data.LastTaskResult); Assert.Null(data.LastRunTime);
        Assert.Equal(new[] { "Connect", "GetFolder", "GetTask" }, service.Calls);
        Assert.Equal(count == 1 ? 1 : 0, service.Task.Definition.Actions.ItemCalls);
        Assert.Equal(count == 1 && type == 0 ? @"\\offline.invalid\never-execute.exe" : null, data.Executable);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void ClrMappedMissingExceptionsAreAbsentOnlyInLookup(bool taskStage, bool directory)
    {
        Exception failure = directory ? new DirectoryNotFoundException("synthetic") : new FileNotFoundException("synthetic");
        var service = new FakeScheduler { LookupFailure = taskStage ? null : failure, TaskFailure = taskStage ? failure : null };
        Assert.False(StartupRegistrationReader.ReadBoot(() => service).Registered);
        Assert.Equal(taskStage ? 3 : 2, service.Calls.Count);
    }

    [Fact]
    public void MissingMetadataAfterSuccessfulLookupIsNotAnAbsentTask()
    {
        var service = new FakeScheduler(); service.Task.PropertyFailure = new FileNotFoundException("synthetic");
        Assert.Throws<FileNotFoundException>(() => StartupRegistrationReader.ReadBoot(() => service));
        Assert.Equal(new[] { "Connect", "GetFolder", "GetTask" }, service.Calls);
    }

    private static void WithOwnedKey(Action<RegistryKey> action)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = @"Software\SystemAutorun.Registration.Tests." + id;
        try { using var key = Registry.CurrentUser.CreateSubKey(path); action(key); }
        finally
        {
            if (!Guid.TryParseExact(id, "N", out _) || path != @"Software\SystemAutorun.Registration.Tests." + id)
            { throw new InvalidOperationException("Non-owned registry fixture."); }
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    // Public managed doubles exercise the exact COM getter sequence, not actual native COM.
    public sealed class FakeScheduler
    {
        public int? Failure { get; set; }
        public Exception? LookupFailure { get; set; }
        public Exception? TaskFailure { get; set; }
        public List<string> Calls { get; } = [];
        public FakeTask Task { get; } = new();
        public void Connect() => Calls.Add("Connect");
        public FakeFolder GetFolder(string path)
        {
            Assert.Equal(@"\Vigil", path); Calls.Add("GetFolder");
            if (Failure is { } code) { throw new COMException("synthetic", code); }
            if (LookupFailure is not null) { throw LookupFailure; }
            return new(Calls, Task, TaskFailure);
        }
    }
    public sealed class FakeFolder(List<string> calls, FakeTask task, Exception? failure)
    {
        public FakeTask GetTask(string name)
        { Assert.Equal("Boot", name); calls.Add("GetTask"); if (failure is not null) { throw failure; } return task; }
    }
    public sealed class FakeTask
    {
        public Exception? PropertyFailure { get; set; }
        public bool Enabled { get { if (PropertyFailure is not null) { throw PropertyFailure; } return false; } }
        public int State => 3;
        public DateTime LastRunTime => new(1899, 12, 30);
        public int LastTaskResult => 7;
        public DateTime NextRunTime => new(1899, 12, 30);
        public int NumberOfMissedRuns => 0;
        public FakeDefinition Definition { get; } = new();
    }
    public sealed class FakeDefinition { public FakeActions Actions { get; } = new(); }
    public sealed class FakeActions
    {
        public int Count { get; set; } = 1;
        public int ItemCalls { get; private set; }
        public FakeAction Action { get; } = new();
        public FakeAction Item(int index) { Assert.Equal(1, index); ItemCalls++; return Action; }
    }
    public sealed class FakeAction
    {
        public int Type { get; set; }
        public string Path => @"\\offline.invalid\never-execute.exe";
        public string Arguments => "--must-not-execute";
    }
}
