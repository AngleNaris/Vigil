using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ColorStateAssistTests
{
    [Fact]
    public Task NestedFramesUseTheInnermostHitAndPaddingRemainsHoverable() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        var application = EnsureApplication();
        var style = Assert.IsType<Style>(application.FindResource("InteractiveFrameBorderStyle"));
        var content = new TextBlock { Text = "fixture" };
        var inner = new Border { Style = style, BorderThickness = new Thickness(1), Child = content };
        var outer = new Border { Style = style, Width = 160, Height = 100, Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = inner };
        var window = Window(outer);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
            Assert.Same(inner, FrameHoverAssist.NearestFrame(content));
            var paddingHit = VisualTreeHelper.HitTest(outer, new System.Windows.Point(6, 6));
            Assert.NotNull(paddingHit); Assert.Same(outer, FrameHoverAssist.NearestFrame(paddingHit.VisualHit));
            Assert.Equal(Colors.Transparent, Brush(outer.Background).Color);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SelectionUsesOwnedBrushesAndCanReverseWithoutChangingGeometry() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var application = EnsureApplication();
            var shared = Assert.IsType<SolidColorBrush>(application.FindResource("Semantic.Brush.InteractionRest"));
            var original = shared.Color;
            var first = new Button { Content = "刷新", Width = 100, Height = 36, FocusVisualStyle = null };
            var second = new Button { Content = "返回", Width = 100, Height = 36, FocusVisualStyle = null };
            ColorStateAssist.SetRole(first, ColorFocusRole.Foreground);
            ColorStateAssist.SetRole(second, ColorFocusRole.Foreground);
            var panel = new StackPanel(); panel.Children.Add(first); panel.Children.Add(second);
            var window = Window(panel);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
                var geometry = (first.ActualWidth, first.ActualHeight, first.BorderThickness, first.Padding);
                Assert.Equal(original, Brush(first.Foreground).Color);
                Assert.NotSame(first.Foreground, second.Foreground); Assert.NotSame(shared, first.Foreground);
                first.Tag = true;
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(Brush(first.Foreground).HasAnimatedProperties);
                    await Task.Delay(30);
                    var interrupted = Brush(first.Foreground).Color;
                    first.Tag = false;
                    Assert.Equal(interrupted, Brush(first.Foreground).Color);
                }
                else first.Tag = false;
                await Task.Delay(170);
                Assert.Equal(original, Brush(first.Foreground).Color);
                Assert.Equal(original, shared.Color); Assert.False(shared.HasAnimatedProperties);
                Assert.Equal(original, Brush(second.Foreground).Color);
                Assert.Equal(geometry, (first.ActualWidth, first.ActualHeight, first.BorderThickness, first.Padding));
                Assert.Null(first.FocusVisualStyle);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task ReducedMotionFinishesImmediatelyAndUnloadingReleasesStateObservers() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            _ = EnsureApplication();
            var button = new Button { Content = "追踪", FocusVisualStyle = null };
            ColorStateAssist.SetRole(button, ColorFocusRole.Foreground);
            var window = Window(button); ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                button.Tag = true;
                var selected = Brush(button.Foreground);
                Assert.Equal(Brush(button.FindResource("Semantic.Brush.InteractionOnActive")).Color, selected.Color);
                Assert.False(selected.HasAnimatedProperties);
                window.Content = null; await Dispatcher.Yield(DispatcherPriority.Loaded);
                var detached = Brush(button.Foreground);
                Assert.Equal(selected.Color, detached.Color);
                button.Tag = false;
                Assert.Same(detached, button.Foreground); Assert.False(detached.HasAnimatedProperties);
                window.Content = button; await Dispatcher.Yield(DispatcherPriority.Loaded);
                Assert.Equal(Brush(button.FindResource("Semantic.Brush.InteractionRest")).Color, Brush(button.Foreground).Color);
                // Focus uses the current theme's color cue: darker on light, lighter on dark, never a frame.
                var rest = Brush(button.Foreground).Color;
                Assert.True(button.Focus()); await Dispatcher.Yield(DispatcherPriority.Loaded);
                var focus = Brush(button.Foreground).Color;
                Assert.Equal(Brush(button.FindResource("Semantic.Brush.InteractionFocus")).Color, focus);
                Assert.NotEqual(rest, focus);
                Assert.NotEqual(rest, Brush(button.FindResource("Semantic.Brush.TextDisabled")).Color);
                Assert.Null(button.FocusVisualStyle);
            }
            finally { window.Close(); }
        });

    private static double Luminance(Color color)
    {
        static double Channel(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    [Fact]
    public Task BorderRoleKeepsBindingAndGeometryAndDisposesWithoutChangingSharedBrushes() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var application = EnsureApplication();
            var border = Brush(application.FindResource("Semantic.Brush.BorderStrong"));
            var button = new Button { Content = "读取", BorderThickness = new Thickness(1), FocusVisualStyle = null };
            var binding = new Binding { Source = border };
            button.SetBinding(Control.BorderBrushProperty, binding);
            ColorStateAssist.SetReduceMotion(button, true);
            ColorStateAssist.SetRole(button, ColorFocusRole.Border);
            var window = Window(button);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                Assert.NotNull(BindingOperations.GetBindingExpression(button, Control.BorderBrushProperty));
                Assert.NotSame(border, button.BorderBrush);
                button.IsEnabled = false;
                Assert.Equal(Brush(button.FindResource("Semantic.Brush.BorderSubtle")).Color, Brush(button.BorderBrush).Color);
                button.IsEnabled = true;
                Assert.Equal(border.Color, Brush(button.BorderBrush).Color);
                Assert.Equal(new Thickness(1), button.BorderThickness);
                ColorStateAssist.SetRole(button, ColorFocusRole.None);
                Assert.Same(border, button.BorderBrush);
                Assert.NotNull(BindingOperations.GetBindingExpression(button, Control.BorderBrushProperty));
                Assert.False(border.HasAnimatedProperties);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task CompositeFrameHoverRecolorsTheExistingBoundBorderWithoutChangingGeometry(bool light) =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            _ = EnsureApplication(); var previous = ThemeService.Preference;
            ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
            var field = new TextBox { Text = "fixture", BorderThickness = new Thickness(0), FocusVisualStyle = null };
            var border = Brush(field.FindResource("Semantic.Brush.BorderSubtle"));
            field.SetBinding(Control.BorderBrushProperty, new Binding { Source = border });
            ColorStateAssist.SetRestingBorderBrush(field, border); ColorStateAssist.SetRole(field, ColorFocusRole.Border);
            ColorStateAssist.SetReduceMotion(field, true);
            var window = Window(field);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                var thickness = field.BorderThickness;
                ColorStateAssist.SetFrameHovered(field, true);
                Assert.Equal(Brush(field.FindResource("Semantic.Brush.InteractionHover")).Color, Brush(field.BorderBrush).Color);
                Assert.Equal(thickness, field.BorderThickness);
                field.IsEnabled = false;
                Assert.Equal(Brush(field.FindResource("Semantic.Brush.BorderSubtle")).Color, Brush(field.BorderBrush).Color);
                field.IsEnabled = true; ColorStateAssist.SetFrameHovered(field, false);
                Assert.Equal(border.Color, Brush(field.BorderBrush).Color);
                Assert.NotNull(BindingOperations.GetBindingExpression(field, Control.BorderBrushProperty));
            }
            finally { window.Close(); ThemeService.Apply(previous, persist: null); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CaptionRoleKeepsBodyBindingsAndGeometryAndRestoresItsOwnBinding(bool reducedMotion) =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var application = EnsureApplication();
            var body = Brush(application.FindResource("Semantic.Brush.TextPrimary"));
            var border = Brush(application.FindResource("Semantic.Brush.BorderSubtle"));
            var originalCaption = Brush(application.FindResource("Semantic.Brush.StatusFailed"));
            var field = new TextBox { Text = "保留正文与异常语义", IsReadOnly = true, BorderThickness = new Thickness(0) };
            field.SetBinding(Control.ForegroundProperty, new Binding { Source = body });
            field.SetBinding(Control.BorderBrushProperty, new Binding { Source = border });
            field.SetBinding(ColorStateAssist.CaptionBrushProperty, new Binding { Source = originalCaption });
            ColorStateAssist.SetRole(field, ColorFocusRole.Caption);
            var caption = new TextBlock { Text = "既有详情标题" };
            caption.SetBinding(TextBlock.ForegroundProperty, new Binding
            { Source = field, Path = new PropertyPath("(0)", ColorStateAssist.CaptionBrushProperty) });
            var panel = new StackPanel(); panel.Children.Add(caption); panel.Children.Add(field);
            var window = Window(panel); ColorStateAssist.SetReduceMotion(window, reducedMotion);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
                var rest = Brush(application.FindResource("Semantic.Brush.InteractionRest")).Color;
                var geometry = (field.RenderSize, field.BorderThickness, field.Padding, field.FontSize, field.FontWeight);
                Assert.Equal(rest, Brush(caption.Foreground).Color);
                Assert.NotSame(originalCaption, ColorStateAssist.GetCaptionBrush(field));
                Assert.Same(ColorStateAssist.GetCaptionBrush(field), caption.Foreground);
                // Tag isolates the shared color/motion contract; it is not an OS focus journey.
                field.Tag = true; await Dispatcher.Yield(DispatcherPriority.DataBind);
                if (!reducedMotion && SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(Brush(caption.Foreground).HasAnimatedProperties);
                    await Task.Delay(30);
                    var interrupted = Brush(caption.Foreground).Color;
                    field.Tag = false;
                    Assert.Equal(interrupted, Brush(ColorStateAssist.GetCaptionBrush(field)).Color);
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    var completion = System.Diagnostics.Stopwatch.StartNew();
                    while (Brush(caption.Foreground).HasAnimatedProperties && completion.Elapsed < TimeSpan.FromSeconds(2))
                    { await Task.Delay(16); }
                    Assert.False(Brush(caption.Foreground).HasAnimatedProperties);
                }
                else
                {
                    Assert.Equal(Brush(application.FindResource("Semantic.Brush.InteractionSelected")).Color, Brush(caption.Foreground).Color);
                    Assert.False(Brush(caption.Foreground).HasAnimatedProperties);
                    field.Tag = false; await Dispatcher.Yield(DispatcherPriority.DataBind);
                }
                Assert.Equal(rest, Brush(caption.Foreground).Color);
                Assert.Same(body, field.Foreground); Assert.Same(border, field.BorderBrush);
                Assert.Equal("保留正文与异常语义", field.Text);
                Assert.Equal(geometry, (field.RenderSize, field.BorderThickness, field.Padding, field.FontSize, field.FontWeight));
                Assert.NotNull(BindingOperations.GetBindingExpression(field, Control.ForegroundProperty));
                Assert.NotNull(BindingOperations.GetBindingExpression(field, Control.BorderBrushProperty));
                ColorStateAssist.SetRole(field, ColorFocusRole.None); await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Same(originalCaption, ColorStateAssist.GetCaptionBrush(field));
                Assert.Same(originalCaption, caption.Foreground);
                Assert.NotNull(BindingOperations.GetBindingExpression(field, ColorStateAssist.CaptionBrushProperty));
                Assert.False(body.HasAnimatedProperties); Assert.False(border.HasAnimatedProperties); Assert.False(originalCaption.HasAnimatedProperties);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task DashboardConsumersShareColorRolesWithoutStateMarkers() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var application = EnsureApplication();
            var icon = new SystemAutorun.App.Controls.LineIcon { Kind = "Refresh" };
            var button = new Button { Content = icon, Style = (Style)application.FindResource("DashboardIconButton") };
            var caption = new TextBlock { Text = "可读的合成服务" };
            var row = new ListBoxItem { Content = caption, Style = (Style)application.FindResource("DashboardSequenceItem") };
            var list = new ListBox(); list.Items.Add(row);
            var panel = new StackPanel(); panel.Children.Add(button); panel.Children.Add(list);
            var window = Window(panel); ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
                Assert.Null(button.FocusVisualStyle); Assert.Null(row.FocusVisualStyle);
                Assert.Equal(ColorFocusRole.Icon, ColorStateAssist.GetRole(button));
                Assert.Equal(ColorFocusRole.Row, ColorStateAssist.GetRole(row));
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.IconRest")).Color, Brush(button.Foreground).Color);
                Assert.Null(button.Template.FindName("KeyboardMarker", button));
                Assert.Null(row.Template.FindName("SelectionMarker", row));
                Assert.Null(row.Template.FindName("KeyboardMarker", row));
                var surface = Assert.IsType<Border>(row.Template.FindName("Row", row));
                var geometry = (row.ActualWidth, row.ActualHeight, surface.BorderThickness);
                button.Tag = true; row.IsSelected = true;
                var selected = Brush(application.FindResource("Semantic.Brush.InteractionOnActive")).Color;
                Assert.Equal(selected, Brush(button.Foreground).Color); Assert.Equal(selected, Brush(icon.Foreground).Color);
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.InteractionActive")).Color,
                    Brush(Assert.IsType<Border>(button.Template.FindName("Surface", button)).Background).Color);
                var rowSelected = Brush(application.FindResource("Semantic.Brush.InteractionOnActive")).Color;
                Assert.Equal(rowSelected, Brush(row.Foreground).Color); Assert.Equal(rowSelected, Brush(caption.Foreground).Color);
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.InteractionActive")).Color, Brush(surface.Background).Color);
                Assert.Equal(geometry, (row.ActualWidth, row.ActualHeight, surface.BorderThickness));
                // Selection turns red/white; muted (disabled service) is dimmer at rest and stays distinct while readable when selected.
                row.IsSelected = false; ColorStateAssist.SetIsMuted(row, true);
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.RowMuted")).Color, Brush(caption.Foreground).Color);
                row.IsSelected = true;
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.InteractionOnActive")).Color, Brush(caption.Foreground).Color);
                row.IsSelected = false; ColorStateAssist.SetIsMuted(row, false);
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.RowRest")).Color, Brush(caption.Foreground).Color);
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.BorderSubtle")).Color, Brush(surface.BorderBrush).Color);
                row.IsSelected = true; Assert.True(row.Focus());
                Assert.Equal(rowSelected, Brush(caption.Foreground).Color);
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.RowFocusBorder")).Color, Brush(surface.BorderBrush).Color);
                Assert.Equal(geometry, (row.ActualWidth, row.ActualHeight, surface.BorderThickness));
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task RoutedFocusReassertsTemplateReplacementsAndUnloadingDetachesItsObservers() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var application = EnsureApplication();
            var field = new Control { Focusable = false }; ColorStateAssist.SetRole(field, ColorFocusRole.Border);
            var window = Window(field); ColorStateAssist.SetReduceMotion(window, true);
            var replacement = new SolidColorBrush(Colors.Magenta);
            void Notify(RoutedEvent notification) => field.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, 0, null, field) { RoutedEvent = notification });
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                // Isolate the post-template routed observer; this is not an OS focus journey.
                Assert.False(field.IsKeyboardFocusWithin);
                foreach (var notification in new[] { System.Windows.Input.Keyboard.GotKeyboardFocusEvent, System.Windows.Input.Keyboard.LostKeyboardFocusEvent })
                {
                    field.SetCurrentValue(Control.BorderBrushProperty, replacement);
                    Notify(notification);
                    Assert.NotSame(replacement, field.BorderBrush);
                    Assert.Equal(Brush(application.FindResource("Semantic.Brush.BorderStrong")).Color, Brush(field.BorderBrush).Color);
                }
                window.Close(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                field.SetCurrentValue(Control.BorderBrushProperty, replacement);
                Notify(System.Windows.Input.Keyboard.GotKeyboardFocusEvent);
                Notify(System.Windows.Input.Keyboard.LostKeyboardFocusEvent);
                Assert.Same(replacement, field.BorderBrush);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task SharedInputsPreserveTheirBordersAndNeverRenderMaterialDecorations() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var application = EnsureApplication();
            var field = new TextBox { Text = "保留输入值" };
            var combo = new ComboBox { ItemsSource = new[] { "Windows", "WSL" }, SelectedIndex = 0 };
            var check = new CheckBox { Content = "启用追踪" };
            var grid = new DataGrid { Height = 130, ItemsSource = new[] { "第一行", "第二行" }, SelectedIndex = 0 };
            grid.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Binding(".") });
            var panel = new StackPanel(); panel.Children.Add(field); panel.Children.Add(combo); panel.Children.Add(check); panel.Children.Add(grid);
            var window = Window(panel); window.Height = 500; ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
                foreach (var control in new Control[] { field, combo })
                {
                    Assert.Null(control.FocusVisualStyle);
                    Assert.Equal(ColorFocusRole.Border, ColorStateAssist.GetRole(control));
                    Assert.Equal(Visibility.Collapsed, MaterialDesignThemes.Wpf.TextFieldAssist.GetDecorationVisibility(control));
                    var permanent = Assert.IsType<Border>(control.Template.FindName("OuterBorder", control));
                    Assert.Equal(new Thickness(0, 0, 0, 1), permanent.BorderThickness);
                    Assert.Null(control.Template.FindName("Underline", control));
                }
                combo.IsEditable = true; await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
                Assert.Null(combo.Template.FindName("Underline", combo));
                Assert.Null(check.FocusVisualStyle); Assert.Equal(ColorFocusRole.Foreground, ColorStateAssist.GetRole(check));
                Assert.Null(check.Template.FindName("InteractionEllipse", check)); Assert.Null(check.Template.FindName("halo", check));
                var checkGeometry = check.RenderSize;
                check.IsChecked = true; window.UpdateLayout();
                Assert.Equal(checkGeometry, check.RenderSize); Assert.True(check.IsChecked);
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.TextPrimary")).Color, Brush(check.Foreground).Color);
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Null(row.FocusVisualStyle); Assert.Null(row.Template.FindName("AccentBar", row));
                Assert.Equal(ColorFocusRole.Row, ColorStateAssist.GetRole(row));
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.InteractionOnActive")).Color, Brush(row.Foreground).Color);
                Assert.Equal(new Thickness(0, 0, 0, 1), Assert.IsType<Border>(row.Template.FindName("RowBorder", row)).BorderThickness);
                Assert.Equal("保留输入值", field.Text); Assert.Equal("Windows", combo.SelectedItem);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task DataGridSelectionChangesWithoutKeyboardFocusUpdateBothRowColors() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var application = EnsureApplication();
            var grid = new DataGrid { ItemsSource = new[] { "第一行", "第二行" }, Height = 150 };
            grid.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Binding(".") });
            var window = Window(grid); ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
                var first = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
                var second = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.Same(System.Windows.Controls.Primitives.Selector.SelectedEvent, DataGridRow.SelectedEvent);
                Assert.Same(System.Windows.Controls.Primitives.Selector.UnselectedEvent, DataGridRow.UnselectedEvent);
                var rest = Brush(application.FindResource("Semantic.Brush.RowRest")).Color;
                var selected = Brush(application.FindResource("Semantic.Brush.InteractionOnActive")).Color;
                grid.SelectedIndex = 0; await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.False(grid.IsKeyboardFocusWithin); Assert.Equal(selected, Brush(first.Foreground).Color);
                grid.SelectedIndex = 1; await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.False(grid.IsKeyboardFocusWithin); Assert.Equal(rest, Brush(first.Foreground).Color);
                Assert.Equal(selected, Brush(second.Foreground).Color);
                grid.SelectedIndex = -1; await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(rest, Brush(second.Foreground).Color);
                System.Windows.Controls.Primitives.DataGridColumnHeader? Header(DependencyObject parent)
                {
                    if (parent is System.Windows.Controls.Primitives.DataGridColumnHeader { Column: not null } header) return header;
                    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                        if (Header(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
                    return null;
                }
                var header = Assert.IsType<System.Windows.Controls.Primitives.DataGridColumnHeader>(Header(grid));
                header.Focusable = false; await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(ColorFocusRole.None, ColorStateAssist.GetRole(header));
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.TextSecondary")).Color, Brush(header.Foreground).Color);
                header.Focusable = true; await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(ColorFocusRole.Foreground, ColorStateAssist.GetRole(header));
                Assert.Equal(Brush(application.FindResource("Semantic.Brush.InteractionRest")).Color, Brush(header.Foreground).Color);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ValidationPresentationMatchesTheSameLibraryControlWithoutTheColorHook(bool combo) =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication();
            Control Create() => combo
                ? new ComboBox { IsEditable = true, ItemsSource = new[] { "fixture" } }
                : new TextBox();
            var field = Create(); var stock = Create(); ColorStateAssist.SetRole(stock, ColorFocusRole.None);
            var property = combo ? ComboBox.TextProperty : TextBox.TextProperty;
            var bindings = new[] { field, stock }.Select(control => control.SetBinding(property,
                new Binding(".") { Source = "fixture", Mode = BindingMode.OneWay })).ToArray();
            var panel = new StackPanel(); panel.Children.Add(field); panel.Children.Add(stock);
            var window = Window(panel); ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); window.UpdateLayout();
                var geometry = field.RenderSize;
                foreach (var binding in bindings)
                    Validation.MarkInvalid(binding, new ValidationError(new ExceptionValidationRule(), binding, "Fixture validation error", null));
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.True(Validation.GetHasError(field)); Assert.True(Validation.GetHasError(stock));
                Assert.Equal(Brush(stock.BorderBrush).Color, Brush(field.BorderBrush).Color);
                ColorStateAssist.SetReduceMotion(window, false);
                Assert.Equal(Brush(stock.BorderBrush).Color, Brush(field.BorderBrush).Color);
                Assert.Equal(geometry, field.RenderSize);
                foreach (var binding in bindings) Validation.ClearInvalid(binding);
                ColorStateAssist.SetReduceMotion(window, true);
                Assert.False(Validation.GetHasError(field));
                Assert.Equal(Brush(stock.BorderBrush).Color, Brush(field.BorderBrush).Color);
                Assert.Equal("fixture", field.GetValue(property));
            }
            finally { window.Close(); }
        });

    private static SystemAutorun.App.App EnsureApplication()
    {
        if (Application.Current is SystemAutorun.App.App current) return current;
        var application = new SystemAutorun.App.App(runRuntimeStartup: false); application.InitializeComponent();
        return application;
    }
    private static Window Window(object content) => new()
    {
        Title = "Owned color state fixture", Content = content, Width = 420, Height = 240,
        ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
    };
    private static SolidColorBrush Brush(object? value) => Assert.IsType<SolidColorBrush>(value);
}
