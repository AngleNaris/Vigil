using System.IO;
using System.Runtime.InteropServices;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed class StartupComDispatchTests
{
    [Theory]
    [InlineData("method")] [InlineData("getter")] [InlineData("setter")]
    public void ReflectionPreservesTheOriginalFailureIdentityAndHResult(string stage)
    {
        var original = new COMException("owned fixture failure", unchecked((int)0x80070005));
        var target = new ThrowingMembers(original);
        var actual = Assert.Throws<COMException>(() =>
        {
            if (stage == "method") { StartupComDispatch.Command(target, "Command"); }
            else if (stage == "getter") { _ = StartupComDispatch.Get<string>(target, "Value"); }
            else { StartupComDispatch.SetText(target, "Value", "ignored"); }
        });
        Assert.Same(original, actual); Assert.Equal(original.HResult, actual.HResult);
        Assert.Contains(nameof(ThrowingMembers), actual.StackTrace);
    }

    [Theory]
    [InlineData("method")] [InlineData("getter")]
    public void RequiredNullResultsDoNotBecomeInventedMetadata(string stage)
    {
        var target = new NullMembers();
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (stage == "method") { _ = StartupComDispatch.Call(target, "ReturnNull"); }
            else { _ = StartupComDispatch.Get<object>(target, "Value"); }
        });
        Assert.Null(StartupComDispatch.Text(target, "Value"));
    }

    [Fact]
    public async Task RealSchedulerUsesFixedDispatchIncludingIndexedPropertiesWithoutRegisteringOrRunningATask()
    {
        await GuiConfigurationControlTests.RunStaAsync(() =>
        {
            object? service = null, definition = null, actions = null, created = null, item = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
                StartupComDispatch.Command(service, "Connect");
                // NewTask only creates an in-memory definition. Never RegisterTask/Save/Run.
                definition = StartupComDispatch.Call(service, "NewTask", 0);
                actions = StartupComDispatch.Get<object>(definition, "Actions");
                Assert.Equal(0, StartupComDispatch.Get<int>(actions, "Count"));
                created = StartupComDispatch.Call(actions, "Create", 0);
                Assert.Equal(1, StartupComDispatch.Get<int>(actions, "Count"));
                item = StartupComDispatch.Call(actions, "Item", 1);
                Assert.Equal(0, StartupComDispatch.Get<int>(item, "Type"));
                Assert.True(string.IsNullOrEmpty(StartupComDispatch.Text(item, "Path")));
                var neverRun = Path.Combine(Path.GetTempPath(), "SystemAutorun-unsaved-" + Guid.NewGuid().ToString("N"), "never-run.exe");
                StartupComDispatch.SetText(item, "Path", neverRun);
                Assert.Equal(neverRun, StartupComDispatch.Text(item, "Path")); Assert.False(File.Exists(neverRun));
                return Task.CompletedTask;
            }
            finally
            {
                foreach (var value in new[] { item, created, actions, definition, service })
                { StartupManager.KeepComObjectAlive(value); }
            }
        });
    }

    public sealed class ThrowingMembers(Exception failure)
    {
        public void Command() => throw failure;
        public string Value { get => throw failure; set => throw failure; }
    }
    public sealed class NullMembers
    {
        public object? ReturnNull() => null;
        public object? Value => null;
    }
}
