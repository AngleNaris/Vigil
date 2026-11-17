using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

/// <summary>
/// R104 shared dialog shell: one self-drawn caption with a close command, the line icon family,
/// normal-weight text and no inset fills. Synthetic fixtures only; no Agent, SSH or Windows writes.
/// </summary>
[Collection(nameof(WpfWindowCollection))]
public sealed partial class DialogChromeTests
{
    public static TheoryData<string> Dialogs => new() { "add", "remote", "doctor", "task", "message" };

    [Theory]
    [MemberData(nameof(Dialogs))]
    public Task LegacyDialogsShareTheSelfDrawnShellAndCloseThroughItsCommand(string kind) =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var application = new SystemAutorun.App.App(runRuntimeStartup: false); application.InitializeComponent(); }
            WindowTheme.Install();
            var window = Create(kind);
            window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000;
            ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(new Thickness(1), window.BorderThickness);
                Assert.NotNull(System.Windows.Shell.WindowChrome.GetWindowChrome(window));
                var all = Descendants(window).ToArray();
                // Library control templates (combo/sort arrows) are not dialog content; authored icons use LineIcon.
                Assert.DoesNotContain(all.OfType<MaterialDesignThemes.Wpf.PackIcon>(), icon => icon.TemplatedParent is null);
                Assert.All(all.OfType<TextBlock>().Where(text => text.IsVisible), text => Assert.True(text.FontWeight == FontWeights.Normal, $"{kind}: '{text.Text}' is {text.FontWeight}"));
                var close = all.OfType<Button>().Single(button => button.Command == SystemCommands.CloseWindowCommand);
                Assert.Equal("关闭", AutomationProperties.GetName(close)); Assert.Null(close.FocusVisualStyle);
                Assert.Equal(ColorFocusRole.Icon, ColorStateAssist.GetRole(close));
                Assert.True(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(close));
                // Route from the button itself, as a click does; an inactive window has no keyboard focus target.
                Assert.True(SystemCommands.CloseWindowCommand.CanExecute(null, close));
                StartupGuiWindowTests.CaptureStateEvidence(window, $"dialog-{kind}-{(int)window.ActualWidth}x{(int)window.ActualHeight}.png");
                if (window.ResizeMode != ResizeMode.NoResize && window.MinWidth > 0 && window.MinHeight > 0)
                {
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    StartupGuiWindowTests.CaptureStateEvidence(window, $"dialog-{kind}-min-{(int)window.ActualWidth}x{(int)window.ActualHeight}.png");
                }
                SystemCommands.CloseWindowCommand.Execute(null, close); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task AddWorkloadShowsOnlyTheEssentialsUntilAdvancedIsOpened() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var application = new SystemAutorun.App.App(runRuntimeStartup: false); application.InitializeComponent(); }
            WindowTheme.Install();
            var window = new AddProgramWindow
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                // R121: retain the essentials and expose the two independent automation choices; no tracking toggle.
                Assert.False(window.AdvancedExpander.IsExpanded);
                var visible = Descendants(window).OfType<Control>().Where(control => control.IsVisible && control is TextBox or ComboBox or CheckBox)
                    .Select(control => AutomationProperties.GetName(control)).ToArray();
                Assert.Equal(["程序路径", "任务名称", "启动时机", "打开应用时未运行则启动", "进程停止后自动恢复"], visible);
                Assert.False(window.StartOnAppLaunchCheckBox.IsChecked);
                Assert.False(window.AutoRecoverCheckBox.IsChecked);
                Assert.Equal("用户登录", window.LaunchTriggerComboBox.SelectedItem?.GetType().GetProperty("Label")?.GetValue(window.LaunchTriggerComboBox.SelectedItem));
                Assert.Null(window.FindName("ExpectedRunningCheckBox"));
                Assert.True(window.ActualHeight < 480, $"collapsed height {window.ActualHeight}");
                StartupGuiWindowTests.CaptureStateEvidence(window, $"dialog-add-basic-{(int)window.ActualWidth}x{(int)window.ActualHeight}.png");
                // The header lines up with the fields' left edge and uses the body size.
                var header = Descendants(window.AdvancedExpander).OfType<TextBlock>().First(text => text.Text == "高级");
                Assert.Equal(13, header.FontSize);
                Assert.InRange(header.TranslatePoint(new Point(), window).X - window.NameTextBox.TranslatePoint(new Point(), window).X, -1, 3);
                var collapsed = window.ActualHeight;
                window.AdvancedExpander.IsExpanded = true;
                // Shared reduced motion settles immediately; only ordinary layout needs a dispatcher pass.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var disclosure = Assert.IsType<SystemAutorun.App.Controls.DisclosurePresenter>(window.AdvancedExpander.Template.FindName("DisclosureBody", window.AdvancedExpander));
                Assert.Equal(1, disclosure.Progress);
                Assert.False(DependencyPropertyHelper.GetValueSource(disclosure, SystemAutorun.App.Controls.DisclosurePresenter.ProgressProperty).IsAnimated);
                Assert.True(window.ActualHeight > collapsed, $"expanded height {window.ActualHeight} vs {collapsed}");
                Assert.InRange(Math.Abs(window.ActualHeight - window.MaxHeight), 0, 1);
                var scroll = Descendants(window).OfType<ScrollViewer>().First(view => view.Content is StackPanel);
                Assert.True(scroll.ScrollableHeight > 0); // R107 caps height, rather than requiring another 200 DIP of growth.
                scroll.ScrollToBottom(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var lastField = window.HealthTimeoutTextBox.TransformToAncestor(scroll).TransformBounds(new Rect(window.HealthTimeoutTextBox.RenderSize));
                Assert.True(lastField.Top >= 0 && lastField.Bottom <= scroll.ActualHeight + 1, $"Last advanced field is clipped: {lastField}");
                foreach (var name in new[] { "工作负载类型", "启动延迟秒数", "启动参数", "窗口模式", "工作目录", "任务描述", "依赖任务", "健康检查类型", "进程匹配规则", "健康检查超时" })
                { Assert.True(Descendants(window).OfType<Control>().Any(control => control.IsVisible && AutomationProperties.GetName(control) == name), $"advanced field not visible: {name}"); }
                Assert.True(window.ActualHeight <= window.MaxHeight + 1);
                StartupGuiWindowTests.CaptureStateEvidence(window, $"dialog-add-advanced-{(int)window.ActualWidth}x{(int)window.ActualHeight}.png");
                // A Windows service hides the program path; its own target field takes its place.
                window.TaskTypeComboBox.SelectedIndex = 1; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(window.ProgramPathPanel.IsVisible); Assert.True(window.WindowsServiceNameTextBox.IsVisible);
            }
            finally { window.Close(); }
        });

    [Theory]
    // DangerButtonStyle has no consumer; its approved red (#E11D48) is below 4.5:1 on the dark fill even at rest, recorded separately.
    [InlineData("PrimaryButtonStyle")]
    [InlineData(null)]
    public Task TextButtonsHaveNoHoverMaskAndKeepTheirLabelReadableInEveryState(string? key) =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var application = new SystemAutorun.App.App(runRuntimeStartup: false); application.InitializeComponent(); }
            var style = (Style)Application.Current!.FindResource(key ?? (object)typeof(Button));
            var button = new Button { Content = "合成按钮", Style = style };
            var window = new Window { Content = button, Width = 240, Height = 120, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); button.ApplyTemplate();
                // The hover bug was a MaterialDesign mask painted from Foreground over the fill.
                var parts = Descendants(button).ToArray();
                Assert.Single(parts.OfType<Border>());
                Assert.DoesNotContain(parts, part => part.GetType().Namespace?.StartsWith("MaterialDesignThemes", StringComparison.Ordinal) == true);
                // R125 hover foreground and fill are a pair; pressed without hover retains its old state.
                SolidColorBrush Resolve(object value) => value is DynamicResourceExtension dynamic
                    ? (SolidColorBrush)button.FindResource(dynamic.ResourceKey) : (SolidColorBrush)value;
                Assert.True(ColorStateAssist.GetHoverAction(button));
                var hover = AllMultiTriggers(style).First(trigger => trigger.Conditions.Any(item => item.Property == UIElement.IsMouseOverProperty && Equals(item.Value, true))
                    && trigger.Conditions.Any(item => item.Property == UIElement.IsEnabledProperty && Equals(item.Value, true)));
                var hoverFill = Resolve(hover.Setters.OfType<Setter>().Single(setter => setter.Property == Control.BackgroundProperty).Value);
                var hoverForeground = (SolidColorBrush)button.FindResource("Semantic.Brush.InteractionOnHover");
                Assert.Equal(((SolidColorBrush)button.FindResource("Semantic.Brush.InteractionHover")).Color, hoverFill.Color);
                Assert.True(Contrast(hoverForeground.Color, hoverFill.Color) >= 4.5, $"{key ?? "Button"} hover: {Contrast(hoverForeground.Color, hoverFill.Color):0.00}:1");
                var pressedForeground = (SolidColorBrush)button.FindResource("Semantic.Brush.InteractionOnHover");
                var pressed = Resolve(AllTriggers(style).Where(trigger => trigger.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty)
                    .SelectMany(trigger => trigger.Setters.OfType<Setter>()).First(setter => setter.Property == Control.BackgroundProperty).Value);
                Assert.Equal(((SolidColorBrush)button.FindResource("Semantic.Brush.InteractionActiveHover")).Color, pressed.Color);
                Assert.True(Contrast(pressedForeground.Color, pressed.Color) >= 4.5, $"{key ?? "Button"} pressed: {Contrast(pressedForeground.Color, pressed.Color):0.00}:1");
            }
            finally { window.Close(); }
        });

    private static IEnumerable<Trigger> AllTriggers(Style? style)
    {
        for (; style is not null; style = style.BasedOn) { foreach (var trigger in style.Triggers.OfType<Trigger>()) { yield return trigger; } }
    }
    private static IEnumerable<MultiTrigger> AllMultiTriggers(Style? style)
    {
        for (; style is not null; style = style.BasedOn) { foreach (var trigger in style.Triggers.OfType<MultiTrigger>()) { yield return trigger; } }
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double L(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        var (high, low) = L(a) > L(b) ? (L(a), L(b)) : (L(b), L(a));
        return (high + .05) / (low + .05);
    }

    internal static Window Create(string kind) => kind switch
    {
        "add" => new AddProgramWindow(StartupGuiControlPlaneTests.Entry),
        "remote" => new RemoteHostsWindow([CreateFixtureRemoteHost()], editing: CreateFixtureRemoteHost()),
        "doctor" => new DoctorWindow(new DoctorReport([
            new DoctorCheck("合成样例：Agent 管道", true, "隔离夹具；未连接生产 Agent。"),
            new DoctorCheck("合成样例：日志目录", false, "隔离夹具：模拟不可写。")])),
        "task" => new TaskDetailWindow(new TaskStatusDetail(Task(), HealthResult.Fail("合成样例：未运行", "隔离夹具；未读取真实进程。"),
            [], ["[合成] 2026-11-05 09:00:00 启动", "[合成] 2026-11-05 09:00:02 退出码 1"])),
        "info" => Message("Information"),
        _ => Message(),
    };

    private static RemoteHostDefinition CreateFixtureRemoteHost() => new()
    {
        Id = "fixture", Name = "合成只读节点", Enabled = false, Platform = RemoteHostPlatform.Linux, Host = "192.0.2.10",
        UserName = "fixture", Authentication = RemoteAuthenticationMode.SshAgent, ConnectTimeoutSeconds = 5, CommandTimeoutSeconds = 10,
    };

    private static AutorunTask Task() => new("合成任务", AutorunTaskType.Windows, true, "合成描述", @"C:\Fixture\app.exe", [], null, null, null, null,
        [], 0, new HealthCheckDefinition(HealthCheckType.Process, "app.exe", null, null, 5, null, null),
        new LaunchOptions(RunAsMode.CurrentUser, LaunchTrigger.Manual, WindowMode.NoConsole, ProcessLifecycle.Tracked));

    private static Window Message(string kind = "Confirmation")
    {
        // The public helpers are modal; the private constructor renders the same window without blocking.
        var kindType = typeof(MessageDialog).GetNestedType("MessageDialogKind", BindingFlags.NonPublic)!;
        var ctor = typeof(MessageDialog).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return (Window)ctor.Invoke(["合成确认", "合成样例：仅用于对话框外壳回归，不执行任何操作。", Enum.Parse(kindType, kind)]);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
}
