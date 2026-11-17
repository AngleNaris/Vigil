using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Configuration;

namespace SystemAutorun.IntegrationTests;

// Application.Current is process-global. A second STA must not construct a window
// between another fixture's new App(...) and InitializeComponent() resource load.
[CollectionDefinition(nameof(WpfWindowCollection), DisableParallelization = true)]
public sealed class WpfWindowCollection { }

[Collection(nameof(WpfWindowCollection))]
public sealed class StartupGuiWindowTests
{
    // ST-F1 synthetic content, real WPF + real typed Pipe/Agent. Not a native-data
    // fixture. Optional probe keeps THIS owned window alive for real desktop input.
    [Fact]
    public async Task ApprovedReadonlyShellLoadsOnceAndFitsDesktopMatrixWithOnDemandDetails()
    {
        var probe = Environment.GetEnvironmentVariable("SA_STARTUP_GUI_PROBE");
        if (!string.IsNullOrEmpty(probe))
        {
            probe = Path.GetFullPath(probe);
            if (Path.GetDirectoryName(probe) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(probe).StartsWith("SystemAutorun-gui-probe-", StringComparison.Ordinal)
                || !Directory.Exists(probe) || new DirectoryInfo(probe).Attributes.HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException("GUI probe requires an existing ordinary owned temp directory."); }
        }
        var reader = new StartupGuiControlPlaneTests.Reader { Entries = SampleEntries() };
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        if (probe is not null)
        {
            // For manual positive configuration adoption only. This is an ordinary owned
            // data file satisfying the existing existence validation, NOT an executable test.
            var ownedTarget = Path.Combine(server.Root, "owned-not-executed.exe"); await File.WriteAllBytesAsync(ownedTarget, [1]);
            reader.Entries[0] = reader.Entries[0] with { Target = ownedTarget, RawCommand = ownedTarget + " --background", WorkingDirectory = server.Root };
        }
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            using var model = new StartupManagerViewModel(new(server.Client), new(server.Paths.StartupCachePath));
            var window = new StartupManagerWindow(model)
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = probe is null ? -32000 : 80, Top = probe is null ? -32000 : 70,
            };
            if (probe is null) { ColorStateAssist.SetReduceMotion(window, true); }
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await window.InitializationTask;
                Assert.Equal(1, reader.Calls); Assert.Equal(4, model.Entries.Count); Assert.False(model.ShowDetails);
                Assert.False(model.QueryPendingOperationCommand.CanExecute(null));
                Assert.Equal(680, window.Width); Assert.Equal(460, window.Height);
                Assert.Equal(520, window.MinWidth); Assert.Equal(360, window.MinHeight); Assert.False(window.ShowInTaskbar);
                model.SelectedEntry = model.Entries.Single(item => item.Name == "媒体库");
                Assert.Equal("已禁用 · 备份未验证", model.SelectedEntry.StatusDescription);
                Assert.False(model.AddToTasksCommand.CanExecute(null));
                foreach (var viewport in new[] { (Width: 520, Height: 360), (Width: 680, Height: 460), (Width: 960, Height: 640), (Width: 1200, Height: 720) })
                {
                    window.Width = viewport.Width; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.Height = viewport.Height; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    Assert.InRange(Math.Abs(viewport.Width - window.ActualWidth), 0, 1 / dpi.DpiScaleX);
                    Assert.InRange(Math.Abs(viewport.Height - window.ActualHeight), 0, 1 / dpi.DpiScaleY);
                    Assert.Equal(360, window.MinHeight);
                    Assert.True(window.StartupList.ActualWidth >= 500);
                    Assert.True(window.StartupList.ActualHeight >= 200);
                    foreach (var button in Descendants(window).OfType<Button>().Where(button => button.IsVisible))
                    {
                        Assert.Null(button.FocusVisualStyle);
                        var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
                            $"Clipped button: {AutomationProperties.GetName(button)}: {bounds}");
                    }
                    if (probe is not null) { Capture(window, Path.Combine(probe, $"readonly-shell-{viewport.Width}x{viewport.Height}.png")); }
                    CaptureStateEvidence(window, $"startup-selected-{viewport.Width}x{viewport.Height}.png");
                }
                Assert.Equal(new Thickness(1), window.SearchSurface.BorderThickness);
                Assert.Same(window.SearchBox.BorderBrush, window.SearchSurface.BorderBrush);
                Assert.Equal(((SolidColorBrush)window.FindResource("Semantic.Brush.BorderSubtle")).Color, ((SolidColorBrush)window.SearchSurface.BorderBrush).Color);
                model.SearchText = "媒体"; window.UpdateLayout();
                Assert.Equal("媒体库", model.SelectedEntry!.Name); Assert.Equal("媒体", window.SearchBox.Text);
                model.SearchText = ""; window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("媒体库", model.SelectedEntry!.Name);
                var row = Assert.IsType<ListBoxItem>(window.StartupList.ItemContainerGenerator.ContainerFromItem(model.SelectedEntry));
                row.ApplyTemplate();
                Assert.Null(row.FocusVisualStyle); Assert.Equal("媒体库", AutomationProperties.GetName(row));
                var rowSurface = Assert.IsType<Border>(row.Template.FindName("Row", row));
                Assert.Equal(new Thickness(0, 0, 0, 1), rowSurface.BorderThickness);
                Assert.Null(row.Template.FindName("SelectionMarker", row)); Assert.Null(row.Template.FindName("KeyboardMarker", row));
                Assert.Equal(ColorFocusRole.Row, ColorStateAssist.GetRole(row));
                var name = Descendants(row).OfType<TextBlock>().Single(text => text.Text == "媒体库");
                var icon = Assert.Single(Descendants(row).OfType<SystemAutorun.App.Controls.LineIcon>());
                var geometry = (row.RenderSize, rowSurface.BorderThickness, name.FontSize, name.FontWeight, icon.RenderSize);
                var reducedMotion = ColorStateAssist.GetReduceMotion(window); ColorStateAssist.SetReduceMotion(window, true);
                var selectedEntry = model.SelectedEntry!;
                model.SelectedEntry = null; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(((SolidColorBrush)window.FindResource("Semantic.Brush.RowRest")).Color, ((SolidColorBrush)name.Foreground).Color);
                model.SelectedEntry = selectedEntry; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(((SolidColorBrush)window.FindResource("Semantic.Brush.InteractionOnActive")).Color, ((SolidColorBrush)name.Foreground).Color);
                Assert.Same(row.Foreground, name.Foreground); Assert.Same(row.Foreground, icon.Foreground);
                Assert.Equal(((SolidColorBrush)window.FindResource("Semantic.Brush.InteractionActive")).Color, ((SolidColorBrush)rowSurface.Background).Color);
                Assert.Equal(geometry, (row.RenderSize, rowSurface.BorderThickness, name.FontSize, name.FontWeight, icon.RenderSize));
                var metadata = Descendants(row).OfType<TextBlock>().Where(text => text.Text == selectedEntry!.Source || text.Text == selectedEntry.StatusDescription).ToArray();
                Assert.Equal(2, metadata.Length);
                foreach (var text in metadata)
                    Assert.Equal(((SolidColorBrush)window.FindResource("Semantic.Brush.InteractionOnActive")).Color, ((SolidColorBrush)text.Foreground).Color);
                var failedEntry = model.Entries.Single(entry => entry.Name == "旧版驱动配置");
                var failedRow = Assert.IsType<ListBoxItem>(window.StartupList.ItemContainerGenerator.ContainerFromItem(failedEntry));
                var failedStatus = Descendants(failedRow).OfType<TextBlock>().Single(text => text.Text == failedEntry.StatusDescription);
                Assert.Equal(((SolidColorBrush)window.FindResource("Semantic.Brush.StatusFailed")).Color, ((SolidColorBrush)failedStatus.Foreground).Color);
                ColorStateAssist.SetReduceMotion(window, reducedMotion);
                if (probe is not null)
                {
                    window.Width = 1320; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.Height = 820;
                    if (Environment.GetEnvironmentVariable("SA_STARTUP_GUI_PROBE_CASE") == "unknown-receipt")
                    {
                        model.SelectedEntry = model.Entries.Single(item => item.Name == "本地 API");
                        await model.InspectCommand.ExecuteAsync(null);
                        Assert.False(await model.AdoptAsync(new AddTaskRequest("r35-probe-unknown", AutorunTaskType.Windows,
                            Executable: Path.Combine(server.Root, "absent-never-run.exe"), HealthPattern: "absent-never-run.exe"), confirmed: true));
                        Assert.True(model.HasUnknownAdoption); Assert.True(model.QueryPendingOperationCommand.CanExecute(null));
                    }
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += (_, _) => closed.TrySetResult();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"), $"Owned WPF probe\nPID={Environment.ProcessId}\nRoot={server.Root}\nOperationId={model.PendingOperationId}\nDPI={dpi.DpiScaleX},{dpi.DpiScaleY}\nSynthetic ST-F1 metadata; NeverRunExecutor; no production data\n");
                    await closed.Task.WaitAsync(TimeSpan.FromMinutes(15));
                }
            }
            finally { window.Close(); }
        }, timeout: probe is null ? null : TimeSpan.FromMinutes(17));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigurationAdoptionCommitCallbackRetainsTheSameEditableFormOnFailureAndBlocksDuplicateSaves(bool adoption)
    {
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
            var window = new AddProgramWindow(adoption ? StartupGuiControlPlaneTests.Entry : null, request =>
            { calls++; Assert.Equal("保留的名称", request.Name); return pending.Task; })
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                window.NameTextBox.Text = "保留的名称"; window.ArgumentsTextBox.Text = "--draft \"keep me\"";
                var save = Descendants(window).OfType<Button>().Single(button => button.IsDefault);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, calls); Assert.False(window.IsEnabled); window.Close(); Assert.True(window.IsVisible);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, calls);
                pending.TrySetResult(false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(window.IsVisible); Assert.True(window.IsEnabled); Assert.Equal("保留的名称", window.NameTextBox.Text);
                Assert.Equal("--draft \"keep me\"", window.ArgumentsTextBox.Text); Assert.Null(window.DialogResult);
            }
            finally { pending.TrySetResult(false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.Close(); }
        });
    }

    [Fact]
    public async Task UnknownOperationMenuReusesTheSameWindowAndCannotUnlockAdoption()
    {
        var reader = new StartupGuiControlPlaneTests.Reader();
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            using var model = new StartupManagerViewModel(new(server.Client), new(server.Paths.StartupCachePath));
            var window = new StartupManagerWindow(model) { ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await window.InitializationTask;
                model.SelectedEntry = Assert.Single(model.Entries); await model.InspectCommand.ExecuteAsync(null);
                Assert.False(await model.AdoptAsync(new("window-unknown", AutorunTaskType.Windows,
                    Executable: Path.Combine(server.Root, "absent-never-run.exe"), HealthPattern: "absent-never-run.exe"), confirmed: true));
                var originalId = Assert.IsType<string>(model.PendingOperationId);
                Assert.Contains(originalId, model.StatusMessage);
                Assert.True(model.HasUnknownAdoption);
                Assert.False(model.AddToTasksCommand.CanExecute(null));
                Assert.Same(model.AddToTasksCommand, window.AdoptButton.Command);
                Assert.False(window.AdoptButton.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    internal static StartupEntry[] SampleEntries() =>
    [
        new("local-api", "HKCU", "本地 API", @"C:\Apps\LocalApi\LocalApi.exe --background", @"C:\Apps\LocalApi\LocalApi.exe", "--background", @"C:\Apps\LocalApi", @"HKCU\Run", null, false, null, null, false, true, null),
        new("file-index", "Startup", "文件索引", null, @"C:\Apps\FileIndex\FileIndex.exe", "--background", @"C:\Apps\FileIndex", @"C:\owned-fixture\文件索引.lnk", null, true, new string('a', 32), DateTimeOffset.UnixEpoch, false, false, null),
        new("media-library", "Startup", "媒体库", null, @"C:\Apps\MediaLibrary\MediaLibrary.exe", "--background", @"C:\Apps\MediaLibrary", @"C:\owned-fixture\媒体库.lnk", null, true, new string('b', 32), DateTimeOffset.UnixEpoch, false, false, null),
        new("old-driver", "HKLM", "旧版驱动配置", null, null, null, null, @"HKLM\Run", "unsupported_registry_value_kind", false, null, null, true, false, null),
    ];
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
    internal static void CaptureStateEvidence(FrameworkElement window, string filename)
    {
        var output = Environment.GetEnvironmentVariable("SA_STATE_GUI_EVIDENCE");
        if (string.IsNullOrEmpty(output)) { return; }
        var directory = new DirectoryInfo(Path.GetFullPath(output));
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("State evidence requires an existing ordinary owned output directory.");
        Capture(window, Path.Combine(directory.FullName, filename));
    }
    private static void Capture(FrameworkElement window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
