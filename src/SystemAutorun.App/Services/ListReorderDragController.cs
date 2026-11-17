using System.Collections;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using ListBox = System.Windows.Controls.ListBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SystemAutorun.App.Services;

/// <summary>
/// Shared drag-to-reorder controller for sidebar lists: shows a crisp floating row preview
/// under the cursor while dragging and animates displaced rows smoothly via FLIP.
/// </summary>
internal sealed class ListReorderDragController<TItem> where TItem : class
{
    private static readonly Duration SwapDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IEasingFunction SwapEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    private readonly ListBox _listBox;
    private readonly Func<IList<TItem>> _items;
    private readonly Func<int> _minIndex;
    private readonly Func<bool> _canDrag;
    private readonly Action<int, int> _moveItem;
    private readonly Action<TItem> _selectItem;
    private readonly Func<Task> _persistOrder;
    private readonly Action<bool>? _onDragActiveChanged;

    private TItem? _dragItem;
    private ListBoxItem? _dragContainer;
    private DragPreviewAdorner? _adorner;
    private AdornerLayer? _adornerLayer;
    private Point _dragStart;
    private double _grabOffsetY;
    private bool _dragging;
    private bool _orderChanged;

    public ListReorderDragController(
        ListBox listBox,
        Func<IList<TItem>> items,
        Func<int> minIndex,
        Func<bool> canDrag,
        Action<int, int> moveItem,
        Action<TItem> selectItem,
        Func<Task> persistOrder,
        Action<bool>? onDragActiveChanged = null)
    {
        _listBox = listBox;
        _items = items;
        _minIndex = minIndex;
        _canDrag = canDrag;
        _moveItem = moveItem;
        _selectItem = selectItem;
        _persistOrder = persistOrder;
        _onDragActiveChanged = onDragActiveChanged;
    }

    internal TItem? DragItem => _dragItem;
    internal bool IsDragging => _dragging;

    internal void SeedTestDragItem(TItem? item)
    {
        _dragItem = item;
        _dragging = false;
        _orderChanged = false;
    }

    internal void Attach()
    {
        _listBox.PreviewMouseLeftButtonDown += (_, e) => HandlePreviewMouseLeftButtonDown(e);
        _listBox.PreviewMouseMove += (_, e) => HandlePreviewMouseMove(e);
        _listBox.PreviewMouseLeftButtonUp += (_, _) => FinishDrag();
        _listBox.LostMouseCapture += (_, _) => FinishDrag();
    }

    internal void HandlePreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ResetVisualState();
        if (e.ChangedButton != MouseButton.Left || !_canDrag()) { return; }

        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not TItem item) { return; }

        var items = _items();
        var index = items.IndexOf(item);
        if (index < _minIndex() || items.Count - _minIndex() <= 1) { return; }

        _dragItem = item;
        _dragStart = e.GetPosition(_listBox);
        _grabOffsetY = Math.Clamp(e.GetPosition(row).Y, 0, Math.Max(0, row.ActualHeight));
    }

    internal void HandlePreviewMouseMove(MouseEventArgs e)
    {
        if (_dragItem is null) { return; }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag();
            return;
        }

        var current = e.GetPosition(_listBox);
        if (!_dragging)
        {
            if (Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) { return; }
            BeginDragVisuals();
        }

        _adorner?.UpdateOffset(current.Y - _grabOffsetY);
        _ = TryUpdateOrder(current.Y);
    }

    internal bool TryUpdateOrder(double pointerY)
    {
        if (_dragItem is null) { return false; }
        var items = _items();
        var minIndex = _minIndex();
        var currentIndex = items.IndexOf(_dragItem);
        if (currentIndex < minIndex) { return false; }

        var targetIndex = currentIndex;
        for (var i = minIndex; i < items.Count; i++)
        {
            if (i == currentIndex) { continue; }
            if (_listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible)
            { continue; }

            var top = container.TranslatePoint(new Point(0, 0), _listBox).Y;
            var height = container.ActualHeight;
            if (i < currentIndex && pointerY < top + height * 0.45)
            {
                targetIndex = i;
                break;
            }
            if (i > currentIndex && pointerY > top + height * 0.55)
            {
                targetIndex = i;
            }
        }

        targetIndex = Math.Clamp(targetIndex, minIndex, items.Count - 1);
        if (targetIndex == currentIndex) { return false; }

        var reduceMotion = ColorStateAssist.GetReduceMotion(_listBox);
        var beforeTops = new Dictionary<TItem, double>();
        if (!reduceMotion)
        {
            for (var i = minIndex; i < items.Count; i++)
            {
                if (_listBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c && c.IsVisible)
                {
                    beforeTops[items[i]] = c.TranslatePoint(new Point(0, 0), _listBox).Y;
                    c.RenderTransform = null;
                }
            }
        }

        _moveItem(currentIndex, targetIndex);
        _selectItem(_dragItem);
        _orderChanged = true;
        _listBox.UpdateLayout();

        if (_dragging)
        {
            ApplySlotDimming();
        }

        if (!reduceMotion && beforeTops.Count > 0)
        {
            foreach (var (item, oldY) in beforeTops)
            {
                if (_listBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem moved || !moved.IsVisible)
                { continue; }

                var newY = moved.TranslatePoint(new Point(0, 0), _listBox).Y;
                var delta = oldY - newY;
                if (Math.Abs(delta) < 0.5) { continue; }

                var transform = new TranslateTransform(0, delta);
                moved.RenderTransform = transform;
                var animation = new DoubleAnimation(delta, 0, SwapDuration)
                {
                    EasingFunction = SwapEase,
                    FillBehavior = FillBehavior.Stop,
                };
                var captured = moved;
                animation.Completed += (_, _) =>
                {
                    if (ReferenceEquals(captured.RenderTransform, transform))
                    {
                        captured.RenderTransform = null;
                    }
                };
                transform.BeginAnimation(TranslateTransform.YProperty, animation);
            }
        }

        return true;
    }

    internal void FinishDrag()
    {
        var shouldPersist = _orderChanged;
        ResetVisualState(willPersist: shouldPersist);
        if (_listBox.IsMouseCaptured) { _listBox.ReleaseMouseCapture(); }
        if (shouldPersist)
        {
            _ = _persistOrder();
        }
    }

    private void BeginDragVisuals()
    {
        _dragging = true;
        _onDragActiveChanged?.Invoke(true);
        _ = _listBox.CaptureMouse();

        if (_dragItem is null) { return; }
        _selectItem(_dragItem);
        _listBox.UpdateLayout();

        if (_listBox.ItemContainerGenerator.ContainerFromItem(_dragItem) is not ListBoxItem container
            || container.ActualWidth < 1 || container.ActualHeight < 1)
        { return; }

        var size = new Size(container.ActualWidth, container.ActualHeight);
        var snapshot = CaptureContainer(container, size);
        var layer = AdornerLayer.GetAdornerLayer(_listBox);
        if (snapshot is not null && layer is not null)
        {
            var borderBrush = _listBox.TryFindResource("Semantic.Brush.FocusAccent") as Brush
                ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0x1D, 0x48));
            _adorner = new DragPreviewAdorner(_listBox, snapshot, size, borderBrush);
            _adornerLayer = layer;
            layer.Add(_adorner);
        }

        ApplySlotDimming();
    }

    private void ApplySlotDimming()
    {
        if (_dragContainer is not null)
        {
            _dragContainer.ClearValue(UIElement.OpacityProperty);
            _dragContainer = null;
        }

        if (_dragItem is not null && _listBox.ItemContainerGenerator.ContainerFromItem(_dragItem) is ListBoxItem current)
        {
            _dragContainer = current;
            current.Opacity = 0.25;
        }
    }

    private void ResetVisualState(bool willPersist = false)
    {
        if (_adorner is not null && _adornerLayer is not null)
        {
            _adornerLayer.Remove(_adorner);
        }

        _adorner = null;
        _adornerLayer = null;

        if (_dragContainer is not null)
        {
            _dragContainer.ClearValue(UIElement.OpacityProperty);
            _dragContainer = null;
        }

        if (_dragging && !willPersist)
        {
            _onDragActiveChanged?.Invoke(false);
        }

        _dragItem = null;
        _dragging = false;
        _orderChanged = false;
    }

    private BitmapSource? CaptureContainer(ListBoxItem container, Size size)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(container);
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var host = new DrawingVisual();
            using (var dc = host.RenderOpen())
            {
                var bg = _listBox.TryFindResource("Semantic.Brush.InteractionActive") as Brush
                    ?? _listBox.TryFindResource("Semantic.Brush.SurfaceHover") as Brush
                    ?? System.Windows.Media.Brushes.Transparent;
                dc.DrawRectangle(bg, null, new Rect(0, 0, size.Width, size.Height));
                dc.DrawRectangle(new VisualBrush(container) { Stretch = Stretch.None }, null, new Rect(0, 0, size.Width, size.Height));
            }

            bitmap.Render(host);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T)
        {
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return current as T;
    }

    private sealed class DragPreviewAdorner : Adorner
    {
        private readonly ImageSource _snapshot;
        private readonly Size _itemSize;
        private readonly Pen _borderPen;
        private double _offsetY;

        public DragPreviewAdorner(UIElement adornedElement, ImageSource snapshot, Size itemSize, Brush borderBrush)
            : base(adornedElement)
        {
            _snapshot = snapshot;
            _itemSize = itemSize;
            _borderPen = new Pen(borderBrush, 1);
            if (_borderPen.CanFreeze) { _borderPen.Freeze(); }
            IsHitTestVisible = false;
        }

        public void UpdateOffset(double offsetY)
        {
            _offsetY = offsetY;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var maxTop = Math.Max(0, AdornedElement.RenderSize.Height - _itemSize.Height);
            var top = Math.Clamp(_offsetY, 0, maxTop);
            var rect = new Rect(0, top, _itemSize.Width, _itemSize.Height);
            drawingContext.PushOpacity(0.94);
            drawingContext.DrawImage(_snapshot, rect);
            drawingContext.Pop();
            drawingContext.DrawRectangle(null, _borderPen, new Rect(0.5, top + 0.5, Math.Max(0, _itemSize.Width - 1), Math.Max(0, _itemSize.Height - 1)));
        }
    }
}
