using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;
using Path = System.IO.Path;
using ThreadTask = System.Threading.Tasks.Task;

namespace SystemAutorun.IntegrationTests;

public sealed partial class DialogChromeTests
{
    public static TheoryData<string, string> FormCloseCases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var kind in new[] { "add", "remote", "task", "message" })
                foreach (var path in new[] { "normal", "reduced", "reverse", "veto", "hide", "owner" })
                    cases.Add(kind, path);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(FormCloseCases))]
    public Task FormMotionKeepsDraftUntilActualClose(string kind, string path) => WithFormOwner(async owner =>
    {
        var window = FormHidden((ToolWindow)Create(kind)); window.Owner = owner;
        var closing = 0; var closed = 0;
        CancelEventHandler guard = (_, args) => { if (++closing == 2 && path == "veto") { args.Cancel = true; } };
        window.Closing += guard; window.Closed += (_, _) => closed++;
        try
        {
            ColorStateAssist.SetReduceMotion(window, path == "reduced");
            window.Show(); await FormSettled(window);
            var text = Descendants(window).OfType<TextBox>().FirstOrDefault();
            if (text is { IsReadOnly: false }) { text.Text = "保留的草稿"; }
            var original = text?.Text; var context = window.DataContext; var height = window.ActualHeight;
            window.Close();
            if (path == "owner") { owner.Close(); }
            else if (window.IsExitPending)
            {
                var surface = (PresencePanel)window.Content;
                Assert.False(surface.IsEnabled); Assert.False(surface.IsHitTestVisible);
                Assert.False(window.ClosureTask.IsCompleted);
                if (path == "reverse") { window.CancelPendingClose(); }
                if (path == "hide") { window.Hide(); window.Show(); }
                if (path is "reverse" or "veto" or "hide")
                {
                    await PresenceMotionTests.Until(() => !window.IsExitPending); await FormSettled(window);
                    await ThreadTask.Delay(200);
                    Assert.True(window.IsVisible); Assert.False(window.ClosureTask.IsCompleted);
                    Assert.Same(context, window.DataContext); Assert.Equal(original, text?.Text);
                    Assert.InRange(Math.Abs(window.ActualHeight - height), 0, 1);
                    Assert.True(surface.IsEnabled); Assert.Equal(1, surface.Opacity);
                    window.Closing -= guard; window.Close();
                }
            }
            await window.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, closed); Assert.False(window.IsVisible);
            Assert.False(((PresencePanel)window.Content).HasAnimatedProperties);
        }
        finally { window.Closing -= guard; FormClose(window); }
    });

    [Theory]
    [InlineData("confirm", false)] [InlineData("cancel", false)] [InlineData("chrome", false)] [InlineData("veto", false)]
    [InlineData("confirm", true)] [InlineData("cancel", true)] [InlineData("chrome", true)] [InlineData("veto", true)]
    public Task MessageMotionReturnsTheChosenResultWhileDispatcherIsAlive(string action, bool reduced) => WithFormOwner(async owner =>
    {
        var window = FormHidden((MessageDialog)Message()); window.Owner = owner;
        ColorStateAssist.SetReduceMotion(window, reduced); var closes = 0;
        CancelEventHandler guard = (_, e) => { if (action == "veto" && ++closes == 2) { e.Cancel = true; } };
        window.Closing += guard;
        var interaction = window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await FormSettled(window);
                if (action == "veto" && !reduced && SystemParameters.ClientAreaAnimation)
                {
                    FormClick(window.ConfirmButton);
                    await PresenceMotionTests.Until(() => !window.IsExitPending); await FormSettled(window);
                    Assert.Null(window.DialogResult); Assert.True(window.IsVisible);
                }
                window.Closing -= guard;
                if (action == "confirm") { FormClick(window.ConfirmButton); if (window.IsExitPending) { window.Close(); } }
                else if (action == "chrome") { SystemCommands.CloseWindow(window); }
                else { FormClick(window.CancelButton); }
                await window.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { window.Closing -= guard; FormClose(window); }
        }).Task.Unwrap();
        Assert.Equal(action == "confirm", window.ShowDialog()); await interaction;
        Assert.False(window.Dispatcher.HasShutdownStarted);
    });

    [Theory]
    [InlineData("success", false)] [InlineData("reject", false)] [InlineData("error", false)]
    [InlineData("success", true)] [InlineData("reject", true)] [InlineData("error", true)]
    public Task AddMotionPreservesSavingGuardAndAllowsFailureRetry(string outcome, bool reduced) => WithFormOwner(async owner =>
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var window = FormHidden(new AddProgramWindow(save: _ => { calls++; return calls == 1 ? pending.Task : ThreadTask.FromResult(true); }));
        window.Owner = owner; ColorStateAssist.SetReduceMotion(window, reduced);
        var interaction = window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await FormSettled(window); window.NameTextBox.Text = "隔离回归草稿"; window.ArgumentsTextBox.Text = "--keep draft";
                var save = Descendants(window).OfType<Button>().Single(button => button.IsDefault);
                FormClick(save); Assert.Equal(1, calls); Assert.False(window.IsEnabled);
                window.Close(); Assert.True(window.IsVisible); Assert.False(window.IsExitPending);
                FormClick(save); Assert.Equal(1, calls);
                if (outcome == "error")
                {
                    pending.SetException(new InvalidOperationException("合成保存失败；未写配置"));
                    await PresenceMotionTests.Until(() => window.OwnedWindows.OfType<MessageDialog>().Any());
                    var error = window.OwnedWindows.OfType<MessageDialog>().Single(); await FormSettled(error);
                    Assert.Contains("输入已保留", error.TitleText.Text); FormClick(error.ConfirmButton); await error.ClosureTask;
                }
                else { pending.SetResult(outcome == "success"); }
                if (outcome != "success")
                {
                    await PresenceMotionTests.Until(() => window.IsEnabled);
                    Assert.True(window.IsVisible); Assert.False(window.IsExitPending); Assert.Null(window.DialogResult);
                    Assert.Equal("隔离回归草稿", window.NameTextBox.Text); Assert.Equal("--keep draft", window.ArgumentsTextBox.Text);
                    FormClick(save);
                }
                await window.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(outcome == "success" ? 1 : 2, calls);
            }
            finally { pending.TrySetResult(false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); FormClose(window); }
        }).Task.Unwrap();
        Assert.True(window.ShowDialog()); await interaction; Assert.False(window.Dispatcher.HasShutdownStarted);
    });

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task RemoteCloseStillWinsAgainstLateSaveAndDoesNotOpenAnError(bool fail, bool afterClosed) => WithFormOwner(async owner =>
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var tempKnownHosts = Path.Combine(Path.GetTempPath(), $"SystemAutorun-remote-motion-{Guid.NewGuid():N}.known_hosts");
        await File.WriteAllTextAsync(tempKnownHosts, "# synthetic known hosts; no SSH connection\n");
        var editing = new RemoteHostDefinition
        {
            Id = "fixture-motion", Name = "合成节点", Enabled = false, Platform = RemoteHostPlatform.Linux,
            Host = "192.0.2.10", UserName = "fixture", Authentication = RemoteAuthenticationMode.SshAgent,
            KnownHostsFile = tempKnownHosts, ConnectTimeoutSeconds = 5, CommandTimeoutSeconds = 10,
        };
        var window = FormHidden(new RemoteHostsWindow([editing], _ => { calls++; return pending.Task; }, editing)); window.Owner = owner;
        var interaction = window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await FormSettled(window); FormClick(window.SaveSettingsButton);
                Assert.Equal(1, calls); Assert.False(window.IsEnabled);
                window.Close(); // RemoteHosts intentionally has no Add-style saving guard.
                if (afterClosed) { await window.ClosureTask; }
                if (fail) { pending.SetException(new InvalidOperationException("合成迟到失败")); } else { pending.SetResult(); }
                await PresenceMotionTests.Until(() => window.IsEnabled); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Empty(window.OwnedWindows.OfType<MessageDialog>());
                await window.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, calls);
            }
            finally { pending.TrySetResult(); FormClose(window); }
        }).Task.Unwrap();
        try { Assert.False(window.ShowDialog()); await interaction; }
        finally { File.Delete(tempKnownHosts); }
    });

    public static TheoryData<string, bool, double, double> FormGeometryCases
    {
        get
        {
            var cases = new TheoryData<string, bool, double, double>();
            foreach (var kind in new[] { "add", "remote", "task", "message" })
                foreach (var light in new[] { false, true })
                    foreach (var size in new[] { (920d, 580d), (960d, 620d), (1320d, 820d) })
                        cases.Add(kind, light, size.Item1, size.Item2);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(FormGeometryCases))]
    public Task FormMotionGeometryAndThemes(string kind, bool light, double width, double height) => WithFormOwner(async owner =>
    {
        owner.Width = width; owner.Height = height; var previous = ThemeService.Preference;
        ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
        var window = FormHidden((ToolWindow)Create(kind)); window.Owner = owner;
        try
        {
            window.Show(); await FormSettled(window);
            if (window is AddProgramWindow add) { add.AdvancedExpander.IsExpanded = true; await ThreadTask.Delay(200); await FormSettled(window); }
            window.UpdateLayout();
            var actions = Descendants(window).OfType<Button>().Where(button => button.IsVisible &&
                (button.IsDefault || button.IsCancel || button.Command == SystemCommands.CloseWindowCommand)).ToArray();
            Assert.NotEmpty(actions); WorkflowMotionAssertions.Geometry(window, owner, actions);
            if (window is RemoteHostsWindow remote)
            {
                var label = Descendants(remote.EnabledCheckBox).OfType<TextBlock>().Single();
                var measure = new TextBlock { Text = label.Text, FontSize = label.FontSize, FontFamily = label.FontFamily };
                measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(label.ActualWidth >= measure.DesiredSize.Width - 1, "Enabled label is clipped inside its control.");
                var scroll = Descendants(remote).OfType<ScrollViewer>().Single(view => view.Content is StackPanel);
                var labelBounds = label.TransformToAncestor(scroll).TransformBounds(new Rect(label.RenderSize));
                Assert.True(labelBounds.Left >= 0 && labelBounds.Right <= scroll.ViewportWidth + 1, $"Enabled label overflows editor: {labelBounds}");
                scroll.ScrollToBottom(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var hint = Descendants(remote).OfType<TextBlock>().Single(text => text.Text.StartsWith("不保存密码，", StringComparison.Ordinal));
                var hintBounds = hint.TransformToAncestor(scroll).TransformBounds(new Rect(hint.RenderSize));
                Assert.True(hintBounds.Left >= 0 && hintBounds.Right <= scroll.ViewportWidth + 1 && hintBounds.Top >= 0 && hintBounds.Bottom <= scroll.ViewportHeight + 1,
                    $"Scrolled hint is clipped: {hintBounds}");
                Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
                var hintMeasure = new TextBlock { Text = hint.Text, FontSize = hint.FontSize, FontFamily = hint.FontFamily };
                hintMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(hint.ActualWidth >= hintMeasure.DesiredSize.Width - 1 || hint.ActualHeight > hint.FontSize * 2,
                    $"Hint must either fit on one line ({hint.ActualWidth:0.#} vs {hintMeasure.DesiredSize.Width:0.#}) or wrap ({hint.ActualHeight:0.#}).");
            }
            StartupGuiWindowTests.CaptureStateEvidence(window, $"form-{kind}-{(light ? "light" : "dark")}-owner-{width}.png");
        }
        finally { FormClose(window); ThemeService.Apply(previous, persist: null); }
    });

    private static Task WithFormOwner(Func<Window, Task> action) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is not SystemAutorun.App.App)
        { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        WindowTheme.Install(); var owner = FormHidden(new Window { Width = 960, Height = 620 });
        try { owner.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await action(owner); }
        finally { owner.Close(); }
    });
    private static T FormHidden<T>(T window) where T : Window
    { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static async Task FormSettled(ToolWindow window)
    {
        await PresenceMotionTests.Until(() => window.IsLoaded);
        await WorkflowMotionAssertions.Settled(window); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static void FormClose(ToolWindow window) { ColorStateAssist.SetReduceMotion(window, true); window.Close(); }
    private static void FormClick(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
}
