using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CommunityToolkit.Mvvm.Input;
using ListBox = System.Windows.Controls.ListBox;

namespace SystemAutorun.App.Services;

/// <summary>Animates the existing tracks, preserving the list, selection and scroll position.</summary>
public static class SidebarAssist
{
    internal const double CollapsedWidth = 52;
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool), typeof(SidebarAssist), new PropertyMetadata(true, Changed));
    private static readonly DependencyPropertyKey ExpandedVisibilityPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "ExpandedVisibility", typeof(Visibility), typeof(SidebarAssist), new PropertyMetadata(Visibility.Visible));
    public static readonly DependencyProperty ExpandedVisibilityProperty = ExpandedVisibilityPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey CollapsedVisibilityPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "CollapsedVisibility", typeof(Visibility), typeof(SidebarAssist), new PropertyMetadata(Visibility.Collapsed));
    public static readonly DependencyProperty CollapsedVisibilityProperty = CollapsedVisibilityPropertyKey.DependencyProperty;
    private static readonly DependencyProperty WidthProperty = DependencyProperty.RegisterAttached(
        "Width", typeof(double), typeof(SidebarAssist), new PropertyMetadata(CollapsedWidth, (target, args) =>
        {
            if (target is Grid { ColumnDefinitions.Count: 2 } grid)
            { grid.ColumnDefinitions[0].Width = new GridLength(Math.Max(CollapsedWidth, (double)args.NewValue)); }
        }));
    private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
        "Generation", typeof(int), typeof(SidebarAssist), new PropertyMetadata(0));

    public static bool GetIsExpanded(DependencyObject target) => (bool)target.GetValue(IsExpandedProperty);
    public static void SetIsExpanded(DependencyObject target, bool value) => target.SetValue(IsExpandedProperty, value);
    public static Visibility GetExpandedVisibility(DependencyObject target) => (Visibility)target.GetValue(ExpandedVisibilityProperty);
    public static Visibility GetCollapsedVisibility(DependencyObject target) => (Visibility)target.GetValue(CollapsedVisibilityProperty);
    public static ICommand ToggleCommand { get; } = new RelayCommand<Grid>(
        grid => SetIsExpanded(grid!, !GetIsExpanded(grid!)), grid => grid?.ColumnDefinitions.Count == 2);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Grid grid) { return; }
        if (!grid.IsLoaded) { grid.Loaded -= Loaded; grid.Loaded += Loaded; }
        else { Apply(grid, true); }
    }

    private static void Loaded(object sender, RoutedEventArgs args)
    {
        var grid = (Grid)sender; grid.Loaded -= Loaded; Apply(grid, false);
    }

    private static void Apply(Grid grid, bool animate)
    {
        if (grid.ColumnDefinitions.Count != 2) { return; }
        LineMotion.StopTree(grid);
        for (DependencyObject? owner = VisualTreeHelper.GetParent(grid); owner is not null; owner = VisualTreeHelper.GetParent(owner))
        { if (owner is FrameworkElement element) { LineMotion.Stop(element); } }
        var expanded = GetIsExpanded(grid);
        var track = grid.ColumnDefinitions[0];
        var from = Math.Max(CollapsedWidth, track.ActualWidth);
        var generation = (int)grid.GetValue(GenerationProperty) + 1;
        grid.SetValue(GenerationProperty, generation);
        grid.BeginAnimation(WidthProperty, null);
        grid.Unloaded -= Unloaded;
        var lists = grid.Children.OfType<FrameworkElement>().Where(child => Grid.GetColumn(child) == 0)
            .SelectMany(FindLists).ToArray();
        if (!animate || !LineMotion.CanPlay(grid)) { Finish(); return; }
        var initialOpacities = lists.ToDictionary(list => list, list => list.IsVisible ? list.Opacity : 0d);
        foreach (var child in grid.Children.OfType<FrameworkElement>().Where(child => Grid.GetColumn(child) == 0))
        { child.ClipToBounds = true; }
        track.MinWidth = CollapsedWidth; track.MaxWidth = double.PositiveInfinity;
        grid.SetValue(WidthProperty, from);
        grid.SetValue(ExpandedVisibilityPropertyKey, Visibility.Visible);
        grid.SetValue(CollapsedVisibilityPropertyKey, Visibility.Collapsed);
        var to = expanded ? Math.Clamp(grid.ActualWidth * .3 / 1.3, 240, 340) : CollapsedWidth;
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var animation = new DoubleAnimation(from, to, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.HoldEnd };
        Timeline.SetDesiredFrameRate(animation, 30);
        animation.Completed += (_, _) => { if ((int)grid.GetValue(GenerationProperty) == generation) { Finish(); } };
        grid.Unloaded += Unloaded;
        grid.BeginAnimation(WidthProperty, animation);
        foreach (var list in lists)
        {
            var opacity = initialOpacities[list];
            list.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, expanded ? 1 : 0, duration)
            { FillBehavior = FillBehavior.HoldEnd });
        }

        void Finish()
        {
            grid.Unloaded -= Unloaded;
            grid.BeginAnimation(WidthProperty, null);
            SetFinal(grid);
            foreach (var list in lists) { list.BeginAnimation(UIElement.OpacityProperty, null); }
        }
    }

    private static IEnumerable<ListBox> FindLists(FrameworkElement root)
    {
        if (root is ListBox list) { yield return list; yield break; }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (VisualTreeHelper.GetChild(root, index) is not FrameworkElement child) { continue; }
            foreach (var found in FindLists(child)) { yield return found; }
        }
    }

    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var grid = (Grid)sender;
        if (!ReferenceEquals(args.OriginalSource, grid)) { return; }
        grid.Unloaded -= Unloaded;
        grid.SetValue(GenerationProperty, (int)grid.GetValue(GenerationProperty) + 1);
        grid.BeginAnimation(WidthProperty, null);
        SetFinal(grid);
        foreach (var list in FindLists(grid)) { list.BeginAnimation(UIElement.OpacityProperty, null); }
    }

    private static void SetFinal(Grid grid)
    {
        var expanded = GetIsExpanded(grid);
        grid.SetValue(ExpandedVisibilityPropertyKey, expanded ? Visibility.Visible : Visibility.Collapsed);
        grid.SetValue(CollapsedVisibilityPropertyKey, expanded ? Visibility.Collapsed : Visibility.Visible);
        var track = grid.ColumnDefinitions[0];
        if (expanded) { track.MaxWidth = 340; track.MinWidth = 240; track.Width = new GridLength(.3, GridUnitType.Star); }
        else { track.MinWidth = CollapsedWidth; track.MaxWidth = CollapsedWidth; track.Width = new GridLength(CollapsedWidth); }
    }
}
