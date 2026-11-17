using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SystemAutorun.App.Controls;
using Brush = System.Windows.Media.Brush;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Size = System.Windows.Size;

namespace SystemAutorun.App.Services;

/// <summary>Stationary entrance: solid lines settle as a whole; semantic regions and local elements enter in sequence.
/// Only animation layers are owned here; state brushes, bindings and live readouts remain untouched.</summary>
public enum EntranceStage { Label, Readout, Plot, Action }

public static class LineMotion
{
    public static readonly DependencyProperty StageProperty = DependencyProperty.RegisterAttached(
        "Stage", typeof(EntranceStage), typeof(LineMotion),
        new FrameworkPropertyMetadata(EntranceStage.Label, FrameworkPropertyMetadataOptions.Inherits));
    public static EntranceStage GetStage(DependencyObject target) => (EntranceStage)target.GetValue(StageProperty);
    public static void SetStage(DependencyObject target, EntranceStage value) => target.SetValue(StageProperty, value);

    public static readonly DependencyProperty RegionProperty = DependencyProperty.RegisterAttached(
        "Region", typeof(int), typeof(LineMotion), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.Inherits));
    public static int GetRegion(DependencyObject target) => (int)target.GetValue(RegionProperty);
    public static void SetRegion(DependencyObject target, int value) => target.SetValue(RegionProperty, value);

    internal const double GrowMilliseconds = 220;
    internal const double SettleMilliseconds = 100;
    internal const double SpreadMilliseconds = 400;
    internal const double RevealMilliseconds = 100;
    internal const double GlyphLagMilliseconds = 100;
    internal const double ReadoutLagMilliseconds = 200;
    internal const double PlotLagMilliseconds = 300;
    internal const double PlotEntranceDelayMilliseconds = 420;
    internal const double ActionLagMilliseconds = 250;
    internal const double MaximumDelayMilliseconds = 720;
    private const int Budget = 120;
    internal const int MaximumCachedFrameSets = 192;
    private readonly record struct FrameKey(Color Color, double Opacity, Color Head, Size Size, Thickness Thickness, bool Fill);
    private static readonly object FrameGate = new();
    private static readonly Dictionary<FrameKey, (Brush[] Frames, LinkedListNode<FrameKey> Node)> FrameSets = [];
    private static readonly LinkedList<FrameKey> FrameUse = new();
    internal static int CachedFrameSetCount { get { lock (FrameGate) { return FrameSets.Count; } } }

    // Only immutable vectors are shared. Timelines, clocks and completion handlers remain per playback.
    private static Brush[] FrozenFrames(FrameKey key, Func<Brush[]> create)
    {
        lock (FrameGate)
        {
            if (FrameSets.TryGetValue(key, out var hit))
            { FrameUse.Remove(hit.Node); FrameUse.AddFirst(hit.Node); return hit.Frames; }
        }
        var frames = create();
        lock (FrameGate)
        {
            if (FrameSets.TryGetValue(key, out var raced)) { return raced.Frames; }
            if (FrameSets.Count >= MaximumCachedFrameSets)
            { var last = FrameUse.Last!; FrameSets.Remove(last.Value); FrameUse.RemoveLast(); }
            FrameSets.Add(key, (frames, FrameUse.AddFirst(key)));
            return frames;
        }
    }

    internal sealed class EntranceSchedule(Size viewport)
    {
        private readonly Dictionary<(double Region, EntranceStage Stage), int> _slots = [];
        internal double RegionDelay(FrameworkElement element, Rect bounds)
        {
            var region = GetRegion(element);
            if (region < 0)
            {
                region = (int)Math.Floor(Math.Clamp(bounds.Top / viewport.Height, 0, .999) * 3) * 2
                    + (bounds.Left >= viewport.Width / 2 ? 1 : 0);
            }
            return Math.Min(SpreadMilliseconds, Math.Max(0, region) * 100d);
        }

        internal double Next(FrameworkElement element, Rect bounds, EntranceStage stage)
        {
            var region = RegionDelay(element, bounds);
            var key = (region, stage);
            _slots.TryGetValue(key, out var slot); _slots[key] = slot + 1;
            var lag = stage switch
            {
                EntranceStage.Readout => ReadoutLagMilliseconds,
                EntranceStage.Plot => PlotLagMilliseconds,
                EntranceStage.Action => ActionLagMilliseconds,
                _ => GlyphLagMilliseconds,
            };
            return Math.Min(MaximumDelayMilliseconds, region + lag + Math.Min(slot, 3) * 45);
        }
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Playback> Active = new();

    public static readonly DependencyProperty IsSuspendedProperty = DependencyProperty.RegisterAttached(
        "IsSuspended", typeof(bool), typeof(LineMotion), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsSuspended(DependencyObject target) => (bool)target.GetValue(IsSuspendedProperty);
    public static void SetIsSuspended(DependencyObject target, bool value) => target.SetValue(IsSuspendedProperty, value);

    internal static bool CanPlay(FrameworkElement root) => root.IsLoaded && root.IsVisible && !GetIsSuspended(root)
        && SystemParameters.ClientAreaAnimation && !ColorStateAssist.GetReduceMotion(root)
        && Window.GetWindow(root)?.WindowState != WindowState.Minimized;

    internal static bool Play(FrameworkElement root)
    {
        using var timing = MotionTiming.Measure("entrance.prepare", root);
        Stop(root);
        var scopedTransition = SurfaceMotion.GetTransitionKey(root) is not null;
        // The initial main-window entrance and a subsequent page entrance must never own the same pixels.
        for (var parent = VisualTreeHelper.GetParent(root); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FrameworkElement ancestor) { continue; }
            if (scopedTransition && Active.TryGetValue(ancestor, out var ancestorPlayback))
            {
                ancestorPlayback.ReleaseSubtree(root);
            }
            else
            {
                Stop(ancestor);
            }
        }
        if (!CanPlay(root)) { return false; }
        root.UpdateLayout();
        var size = root.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) { return false; }
        var head = (root.TryFindResource("Semantic.Brush.Accent") as SolidColorBrush)?.Color ?? Colors.Red;
        var backdrop = (root.TryFindResource("Semantic.Brush.WindowBackground") as SolidColorBrush)?.Color;
        var pending = new List<(UIElement Target, DependencyProperty Property, AnimationTimeline Timeline)>();
        var lines = 0; var glyphs = 0;
        var schedule = new EntranceSchedule(size);
        Collect(root);
        if (pending.Count == 0) { return false; }
        var playback = new Playback(root, pending.Select(item => (item.Target, item.Property)).ToList());
        Active.Add(root, playback);
        var remaining = pending.Count;
        foreach (var (target, property, timeline) in pending)
        {
            // Attach before CreateClock: WPF evaluates a frozen timeline copy.
            timeline.Completed += (_, _) =>
            {
                using var completed = MotionTiming.Measure("entrance.complete", root);
                if (!Active.TryGetValue(root, out var current) || !ReferenceEquals(current, playback)) { return; }
                if (!playback.Targets.Remove((target, property))) { return; }
                target.BeginAnimation(property, null);
                if (--remaining == 0) { Stop(root); }
            };
            var clock = timeline.CreateClock();
            clock.Controller!.Begin(); clock.Controller.Pause();
            playback.Clocks.Add(clock);
            playback.Entries.Add((target, property, clock));
            target.ApplyAnimationClock(property, clock);
            // Resolve time zero after attachment, before the first composition frame can paint a fill.
            clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        }
        return true;

        bool IsAction(DependencyObject element)
        {
            for (var parent = element; parent is not null && !ReferenceEquals(parent, root); parent = VisualTreeHelper.GetParent(parent))
            { if (parent is System.Windows.Controls.Primitives.ButtonBase) { return true; } }
            return false;
        }

        void Collect(FrameworkElement element)
        {
            if (!ReferenceEquals(element, root)) { Stop(element); }
            if (!element.IsVisible || element.Opacity <= 0) { return; }
            Rect bounds;
            try { bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
            catch (InvalidOperationException) { return; }
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) { return; }
            if (element is DashboardPlot plot)
            {
                plot.ClearHover();
                var delay = PlotEntranceDelayMilliseconds + Math.Min(120d, schedule.RegionDelay(element, bounds) * 0.30);
                plot.RequestEntranceSweep(delay);
                return;
            }
            if (!bounds.IntersectsWith(new Rect(size)))
            {
                CollectPlotDescendants(element);
                return;
            }
            var group = schedule.RegionDelay(element, bounds);
            // Native editors and selected row/tab fills are one visual unit. Replaying their template
            // leaves exposed text/background instantly and makes an in-flight hover look like a pulse.
            var surface = element is TextBox or PasswordBox or ComboBox or ListBoxItem or DataGridRow or TabItem
                or System.Windows.Controls.Primitives.ButtonBase;
            if (surface)
            {
                var inkSurface = element;
                if (element is TextBox native && native.Template?.FindName("PART_ContentHost", native) is FrameworkElement content)
                {
                    // Keep the frame visible while it is drawn; hiding the entire editor conceals that earlier phase.
                    inkSurface = content;
                    if (native.Background is SolidColorBrush { Color.A: > 0 } fill
                        && !DependencyPropertyHelper.GetValueSource(native, Control.BackgroundProperty).IsAnimated && lines++ < Budget)
                    { pending.Add((native, Control.BackgroundProperty, MakeFill(fill, group + GlyphLagMilliseconds))); }
                }
                if (glyphs++ < Budget && !DependencyPropertyHelper.GetValueSource(inkSurface, UIElement.OpacityProperty).IsAnimated)
                {
                    var stage = IsAction(element) ? EntranceStage.Action
                        : element is TextBox or PasswordBox or ComboBox ? EntranceStage.Readout : GetStage(element);
                    var delay = schedule.Next(element, bounds, stage);
                    pending.Add((inkSurface, UIElement.OpacityProperty, Reveal(inkSurface.Opacity, delay)));
                }
                if (element is TextBox control && control.BorderThickness != new Thickness(0)
                    && (ColorStateAssist.ResolveRestingBorderBrush(control) ?? control.BorderBrush as SolidColorBrush) is { Color.A: > 0 } borderInk
                    && !DependencyPropertyHelper.GetValueSource(control, Control.BorderBrushProperty).IsAnimated && lines++ < Budget)
                {
                    pending.Add((control, Control.BorderBrushProperty, MakeLines(borderInk, head,
                        control.RenderSize, control.BorderThickness, 40 + group)));
                }
                return; // One native surface, not another reveal for every template descendant.
            }
            if (element is TextBlock or LineIcon or ProgressBar)
            {
                // A rolling number animates a different property and still participates in the entrance.
                if (glyphs++ < Budget && !DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated
                    && DependencyPropertyHelper.GetValueSource(element, SurfaceMotion.TargetOpacityProperty).BaseValueSource == BaseValueSource.Default)
                {
                    var stage = IsAction(element) ? EntranceStage.Action
                        : element is ProgressBar ? EntranceStage.Plot
                        : System.Windows.Data.BindingOperations.IsDataBound(element, SurfaceMotion.RollingTextProperty)
                            ? EntranceStage.Readout : GetStage(element);
                    var delay = schedule.Next(element, bounds, stage);
                    if (element is TextBlock && GlyphReveal.Create(element, delay) is { } reveal)
                    { pending.Add((reveal.Target, UIElement.ClipProperty, reveal.Animation)); }
                    else
                    {
                        pending.Add((element, UIElement.OpacityProperty, Reveal(element.Opacity, delay)));
                    }
                }
                return;
            }
            if (element is System.Windows.Controls.Panel panel
                && panel.Background is SolidColorBrush { Color.A: > 0 } panelFill
                && panelFill.Color != backdrop
                && !DependencyPropertyHelper.GetValueSource(panel, System.Windows.Controls.Panel.BackgroundProperty).IsAnimated && lines++ < Budget)
            {
                pending.Add((panel, System.Windows.Controls.Panel.BackgroundProperty, MakeFill(panelFill, group + GlyphLagMilliseconds)));
            }
            if (element is Border border)
            {
                if (bounds.Height > 3 && border.Background is SolidColorBrush { Color.A: > 0 } fill && fill.Color != backdrop
                    && !DependencyPropertyHelper.GetValueSource(border, Border.BackgroundProperty).IsAnimated && lines++ < Budget)
                {
                    pending.Add((border, Border.BackgroundProperty, MakeFill(fill, group + GlyphLagMilliseconds)));
                }
                var thickness = border.BorderThickness;
                var framed = thickness.Left + thickness.Top + thickness.Right + thickness.Bottom > 0;
                var property = framed ? Border.BorderBrushProperty : Border.BackgroundProperty;
                var lineBrush = framed
                    ? SurfaceMotion.PrepareStrokeForLineMotion(border)
                        ?? ColorStateAssist.ResolveRestingBorderBrush(border)
                        ?? border.GetValue(property) as SolidColorBrush
                    : border.GetValue(property) as SolidColorBrush;
                // Thin meter tracks are lines too; never animate their geometry or sampled values.
                if ((framed || bounds.Height <= 3 && bounds.Width > 12)
                    && lineBrush is { Color.A: > 0 }
                    && !DependencyPropertyHelper.GetValueSource(border, property).IsAnimated && lines++ < Budget)
                {
                    // The outer skeleton leads; local frames follow by region before text/readouts.
                    var delay = GetRegion(element) >= 0 ? group
                        : bounds.Width >= size.Width * .8 || bounds.Height >= size.Height * .8 ? 0 : 40 + group;
                    pending.Add((border, property, MakeLines(lineBrush, head,
                        border.RenderSize, framed ? thickness : new Thickness(0, 0, 0, border.ActualHeight), delay)));
                }
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            { if (VisualTreeHelper.GetChild(element, index) is FrameworkElement child) { Collect(child); } }
        }

        void CollectPlotDescendants(FrameworkElement container)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(container); index++)
            {
                if (VisualTreeHelper.GetChild(container, index) is not FrameworkElement child) { continue; }
                Stop(child);
                if (!child.IsVisible || child.Opacity <= 0) { continue; }
                if (child is DashboardPlot plot)
                {
                    Rect plotBounds;
                    try { plotBounds = plot.TransformToAncestor(root).TransformBounds(new Rect(plot.RenderSize)); }
                    catch (InvalidOperationException) { continue; }
                    if (plotBounds.IsEmpty || plotBounds.Width <= 0 || plotBounds.Height <= 0) { continue; }
                    plot.ClearHover();
                    var delay = PlotEntranceDelayMilliseconds + Math.Min(120d, schedule.RegionDelay(plot, plotBounds) * 0.30);
                    plot.RequestEntranceSweep(delay);
                    continue;
                }
                CollectPlotDescendants(child);
            }
        }
    }

    internal static void Stop(FrameworkElement root)
    {
        var version = (int)root.GetValue(TransitionVersionProperty);
        if (version > 0)
        {
            root.SetValue(TransitionVersionProperty, version + 1);
            root.BeginAnimation(UIElement.OpacityProperty, null);
        }
        if (!Active.TryGetValue(root, out var played)) { return; }
        Active.Remove(root);
        played.Dispose();
    }

    internal static void StopTree(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            Stop(element);
            var version = (int)element.GetValue(TransitionVersionProperty);
            if (version > 0)
            {
                element.SetValue(TransitionVersionProperty, version + 1);
                element.BeginAnimation(UIElement.OpacityProperty, null);
            }
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { StopTree(VisualTreeHelper.GetChild(root, index)); }
    }

    internal static bool IsPlaying(FrameworkElement root) => Active.TryGetValue(root, out _);

    internal static bool HasPlayingAncestor(FrameworkElement root)
    {
        for (var parent = VisualTreeHelper.GetParent(root); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        { if (parent is FrameworkElement ancestor && IsPlaying(ancestor)) { return true; } }
        return false;
    }

    internal static void ReleaseTarget(UIElement target, DependencyProperty property)
    {
        for (DependencyObject? current = target; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement root && Active.TryGetValue(root, out var playback))
            {
                playback.ReleaseTarget(target, property);
            }
        }
    }

    internal static DoubleAnimationUsingKeyFrames Reveal(double rest, double delay)
    {
        KeyTime At(double milliseconds) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + milliseconds));
        return new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop, Duration = TimeSpan.FromMilliseconds(delay + RevealMilliseconds),
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(0, At(0)),
                new LinearDoubleKeyFrame(rest, At(RevealMilliseconds)),
            },
        };
    }

    private static readonly DependencyProperty TransitionVersionProperty = DependencyProperty.RegisterAttached(
        "TransitionVersion", typeof(int), typeof(LineMotion), new PropertyMetadata(0));

    internal static void Transition(FrameworkElement root)
    {
        if (!CanPlay(root)) { return; }
        Stop(root);
        var version = (int)root.GetValue(TransitionVersionProperty) + 1;
        root.SetValue(TransitionVersionProperty, version);
        // Hold the new selection before bindings/layout run, so its fills cannot leak for one frame.
        root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0, Duration.Forever));
        root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != (int)root.GetValue(TransitionVersionProperty)) { return; }
            root.BeginAnimation(UIElement.OpacityProperty, null);
            if (CanPlay(root)) { Play(root); }
        }));
    }

    private static ObjectAnimationUsingKeyFrames MakeFill(SolidColorBrush rest, double delay)
    {
        var animation = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + 120), FillBehavior = FillBehavior.Stop };
        var frames = FrozenFrames(new FrameKey(rest.Color, rest.Opacity, default, default, default, true), () =>
        {
            var result = new Brush[7];
            for (var step = 0; step <= 6; step++)
            {
                var brush = new SolidColorBrush(rest.Color) { Opacity = rest.Opacity * step / 6 };
                brush.Freeze(); result[step] = brush;
            }
            return result;
        });
        for (var step = 0; step <= 6; step++)
        {
            if (step == 0) { animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frames[step], KeyTime.FromTimeSpan(TimeSpan.Zero))); }
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frames[step], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + step * 20))));
        }
        return animation;
    }

    internal static ObjectAnimationUsingKeyFrames MakeLines(SolidColorBrush rest, Color head, Size size, Thickness thickness, double delay, bool reuseFrames = true)
    {
        var animation = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + GrowMilliseconds + SettleMilliseconds), FillBehavior = FillBehavior.Stop };
        // The uncached path measures identical preparation work, not a lower-quality animation baseline.
        Brush[] CreateFrames()
        {
            var stable = rest.CloneCurrentValue(); stable.Freeze();
            var result = new Brush[16];
            for (var step = 0; step <= 11; step++) { result[step] = DrawLines(stable, head, size, thickness, (double)step / 11, 1); }
            for (var step = 1; step <= 4; step++) { result[11 + step] = DrawLines(stable, head, size, thickness, 1, 1 - (double)step / 4); }
            return result;
        }
        var frames = reuseFrames ? FrozenFrames(new FrameKey(rest.Color, rest.Opacity, head, size, thickness, false), CreateFrames) : CreateFrames();
        for (var step = 0; step <= 11; step++)
        {
            if (step == 0) { animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frames[step], KeyTime.FromTimeSpan(TimeSpan.Zero))); }
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frames[step], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + step * GrowMilliseconds / 11))));
        }
        for (var step = 1; step <= 4; step++)
        {
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frames[11 + step], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + GrowMilliseconds + step * SettleMilliseconds / 5))));
        }
        return animation;
    }

    private sealed class Playback : IDisposable
    {
        private readonly FrameworkElement _root;
        private readonly Window? _window;
        private readonly DependencyPropertyDescriptor _motion;
        internal List<(UIElement Target, DependencyProperty Property)> Targets { get; }
        internal List<AnimationClock> Clocks { get; } = [];
        internal List<(UIElement Target, DependencyProperty Property, AnimationClock Clock)> Entries { get; } = [];

        private void FirstRender(object? sender, EventArgs args)
        {
            using var timing = MotionTiming.Measure("entrance.resume", _root);
            CompositionTarget.Rendering -= FirstRender;
            // Show/layout can consume most of a short animation. Anchor every channel to the first visible frame.
            foreach (var clock in Clocks) { clock.Controller!.Resume(); }
        }

        internal Playback(FrameworkElement root, List<(UIElement, DependencyProperty)> targets)
        {
            _root = root; _window = Window.GetWindow(root); Targets = targets;
            if (_window is not null) { _window.StateChanged += WindowStateChanged; }
            _motion = DependencyPropertyDescriptor.FromProperty(ColorStateAssist.ReduceMotionProperty, root.GetType());
            _motion.AddValueChanged(root, MotionChanged);
            root.IsVisibleChanged += VisibilityChanged; root.Unloaded += Unloaded; root.SizeChanged += Resized;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            CompositionTarget.Rendering += FirstRender;
        }

        private void WindowStateChanged(object? sender, EventArgs args) { if (_window?.WindowState == WindowState.Minimized) { Stop(_root); } }
        private void MotionChanged(object? sender, EventArgs args) { if (ColorStateAssist.GetReduceMotion(_root)) { Stop(_root); } }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (args.NewValue is false) { Stop(_root); } }
        private void Unloaded(object sender, RoutedEventArgs args)
        { if (ReferenceEquals(args.OriginalSource, _root)) { Stop(_root); } }
        private void Resized(object sender, SizeChangedEventArgs args)
        {
            if (!args.WidthChanged && SurfaceMotion.GetTransitionKey(_root) is not null) { return; }
            Stop(_root);
        }
        private void SystemChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) { return; }
            if (!_root.Dispatcher.CheckAccess())
            {
                if (!_root.Dispatcher.HasShutdownStarted)
                { _ = _root.Dispatcher.BeginInvoke(new Action(() => { if (!SystemParameters.ClientAreaAnimation) { Stop(_root); } })); }
                return;
            }
            if (!SystemParameters.ClientAreaAnimation) { Stop(_root); }
        }

        internal void ReleaseSubtree(DependencyObject subtree)
        {
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                var (target, property, clock) = Entries[index];
                if (!IsWithin(target, subtree)) { continue; }
                clock.Controller?.Remove();
                target.BeginAnimation(property, null);
                Clocks.Remove(clock);
                Targets.Remove((target, property));
                Entries.RemoveAt(index);
            }
        }

        internal void ReleaseTarget(UIElement target, DependencyProperty property)
        {
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                var entry = Entries[index];
                if (!ReferenceEquals(entry.Target, target) || entry.Property != property) { continue; }
                entry.Clock.Controller?.Remove();
                target.BeginAnimation(property, null);
                Clocks.Remove(entry.Clock);
                Targets.Remove((target, property));
                Entries.RemoveAt(index);
            }
        }

        private static bool IsWithin(DependencyObject target, DependencyObject subtree)
        {
            for (var current = target; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, subtree)) { return true; }
            }
            return false;
        }

        public void Dispose()
        {
            using var timing = MotionTiming.Measure("entrance.dispose", _root);
            if (_window is not null) { _window.StateChanged -= WindowStateChanged; }
            _motion.RemoveValueChanged(_root, MotionChanged);
            _root.IsVisibleChanged -= VisibilityChanged; _root.Unloaded -= Unloaded; _root.SizeChanged -= Resized;
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            CompositionTarget.Rendering -= FirstRender;
            foreach (var (target, property) in Targets) { target.BeginAnimation(property, null); }
            foreach (var clock in Clocks) { clock.Controller!.Remove(); }
            Targets.Clear(); Clocks.Clear();
        }
    }

    // A real brush image per orthogonal edge, not a diagonal mask stretched over a multi-sided Border.
    // The transparent full-size anchor keeps DrawingBrush mapping stable while only part of a line exists.
    internal static Brush DrawLines(SolidColorBrush rest, Color head, Size size, Thickness thickness, double progress, double? tintStrength = null)
    {
        progress = Math.Clamp(progress, 0, 1);
        var strength = Math.Clamp(tintStrength ?? (progress >= 1 ? 0 : 1), 0, 1);
        if (progress >= 1 && strength <= 0) { return rest; }
        // Apply alpha once to the group, so overlapping corners do not darken before handoff to the real Border.
        var drawing = new DrawingGroup { Opacity = rest.Opacity * rest.Color.ScA };
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(size));
            var edges = new List<(Rect Bounds, bool Vertical, bool Reverse)>(4);
            if (thickness.Top > 0) { edges.Add((new Rect(0, 0, size.Width, thickness.Top), false, false)); }
            if (thickness.Right > 0) { edges.Add((new Rect(size.Width - thickness.Right, 0, thickness.Right, size.Height), true, false)); }
            if (thickness.Bottom > 0) { edges.Add((new Rect(0, size.Height - thickness.Bottom, size.Width, thickness.Bottom), false, edges.Count > 0)); }
            if (thickness.Left > 0) { edges.Add((new Rect(0, 0, thickness.Left, size.Height), true, edges.Count > 0)); }
            var length = 0d;
            for (var index = 0; index < edges.Count; index++)
            {
                length += edges[index].Vertical ? edges[index].Bounds.Height : edges[index].Bounds.Width;
            }
            var traced = length * progress;
            var cursor = 0d;
            var ink = new SolidColorBrush(Color.FromScRgb(1,
                (float)(rest.Color.ScR + (head.ScR - rest.Color.ScR) * strength),
                (float)(rest.Color.ScG + (head.ScG - rest.Color.ScG) * strength),
                (float)(rest.Color.ScB + (head.ScB - rest.Color.ScB) * strength)));
            ink.Freeze();
            foreach (var (bounds, vertical, reverse) in edges)
            {
                var total = vertical ? bounds.Height : bounds.Width;
                var visible = Math.Min(total, traced - cursor);
                if (visible <= 0) { break; }
                var region = bounds;
                if (vertical) { region.Height = visible; if (reverse) { region.Y = bounds.Bottom - visible; } }
                else { region.Width = visible; if (reverse) { region.X = bounds.Right - visible; } }
                context.DrawRectangle(ink, null, region);
                cursor += total;
            }
        }
        var brush = new DrawingBrush(drawing) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

}
