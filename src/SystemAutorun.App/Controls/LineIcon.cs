using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace SystemAutorun.App.Controls;

/// <summary>One quiet, stroke-based icon family for the approved dashboard.</summary>
public sealed class LineIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(LineIcon),
        new FrameworkPropertyMetadata("Service", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LineIcon), new FrameworkPropertyMetadata(Brushes.Gray,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly IReadOnlyDictionary<string, Geometry> Glyphs = CreateGlyphs();

    public LineIcon() { Width = 18; Height = 18; IsHitTestVisible = false; }
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    internal static Geometry Glyph(string kind) => Glyphs.TryGetValue(kind, out var geometry) ? geometry : Glyphs["Service"];

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var geometry = Glyph(Kind);
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        if (scale <= 0) { return; }
        context.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        var rounded = Kind is "Help" or "LegendHelp" or "Info" or "Linux" or "MacOs";
        var pen = new Pen(Foreground, Kind == "LegendHelp" ? 1.65 : 1.5)
        {
            LineJoin = rounded ? PenLineJoin.Round : PenLineJoin.Miter,
            StartLineCap = rounded ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = rounded ? PenLineCap.Round : PenLineCap.Flat,
        };
        var filled = Kind is "Stop" or "Play";
        context.DrawGeometry(filled ? Foreground : null, filled ? null : pen, geometry);
        context.Pop();
        context.Pop();
    }

    private static IReadOnlyDictionary<string, Geometry> CreateGlyphs()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Menu"] = "M4,6 H20 M4,12 H20 M4,18 H20",
            ["Plus"] = "M12,5 V19 M5,12 H19",
            ["Search"] = "M20,20 L16,16 M18,10.5 A7.5,7.5 0 1 1 3,10.5 A7.5,7.5 0 1 1 18,10.5",
            ["Filter"] = "M3,4 H21 L14,12 V20 L10,18 V12 Z",
            ["Refresh"] = "M20,9 A8,8 0 1 0 20,15 M20,3 V9 H14",
            ["Host"] = "M3,3 H21 V10 H3 Z M3,14 H21 V21 H3 Z M6,6.5 H8 M6,17.5 H8",
            ["Settings"] = "M9,3 H15 L16,6 L19,7 L21,10 V14 L18,15 L17,18 L14,21 H10 L9,18 L6,17 L3,14 V10 L6,9 L7,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
            ["Minimize"] = "M5,12 H19",
            ["Maximize"] = "M5,5 H19 V19 H5 Z",
            ["Close"] = "M6,6 L18,18 M18,6 L6,18",
            ["Copy"] = "M8,8 H21 V21 H8 Z M16,8 V3 H3 V16 H8",
            ["Stop"] = "M5,5 H19 V19 H5 Z",
            ["Play"] = "M7,4 L20,12 L7,20 Z",
            ["Restart"] = "M5,8 A8,8 0 1 1 4,14 M5,3 V8 H10",
            ["Logs"] = "M4,5 H20 M4,10 H20 M4,15 H15 M4,20 H12",
            ["More"] = "M4,12 H6 M11,12 H13 M18,12 H20",
            ["Clock"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,6 V12 L16,14",
            ["Cpu"] = "M6,6 H18 V18 H6 Z M9,9 H15 V15 H9 Z M9,2 V5 M15,2 V5 M9,19 V22 M15,19 V22 M2,9 H5 M2,15 H5 M19,9 H22 M19,15 H22",
            ["Memory"] = "M2,7 H22 V17 H2 Z M6,10 V14 M10,10 V14 M14,10 V14 M18,10 V14",
            ["Activity"] = "M2,12 H6 L9,4 L13,20 L16,12 H22",
            ["Service"] = "M4,3 H20 V21 H4 Z M8,7 H16 M8,12 H16 M8,17 H13",
            ["Windows"] = "M3,5 L11,4 V11 H3 Z M13,4 L21,3 V11 H13 Z M3,13 H11 V20 L3,19 Z M13,13 H21 V21 L13,20 Z",
            ["Linux"] = "M7.5,19.5 C5.7,16 6.2,11.5 8.2,9 C8.4,4.8 9.8,3 12,3 C14.2,3 15.6,4.8 15.8,9 C17.8,11.5 18.3,16 16.5,19.5 M9,19.5 C9,13.5 15,13.5 15,19.5 M10,6.8 V7.2 M14,6.8 V7.2 M10.3,8.7 L12,10.2 L13.7,8.7 M6,20.5 H10.5 M13.5,20.5 H18",
            ["MacOs"] = "M12.5,5.8 C12.5,3.8 14,2.5 16,2.3 C16,4.3 14.5,5.8 12.5,5.8 Z M12,7.6 C9.5,6.8 6.2,7.2 4.8,10.5 C4.2,13.5 5.2,18.2 7.5,20.5 C8.5,21.5 9.8,21.4 12,20.6 C14.2,21.4 15.5,21.5 16.5,20.5 C17.9,19.1 18.9,17.4 19.5,15.8 C17.1,14.8 17.1,10.8 19.3,9.4 C17.9,7.3 14.6,6.8 12,7.6 Z",
            ["Database"] = "M4,6 A8,3 0 1 0 20,6 A8,3 0 1 0 4,6 M4,6 V18 A8,3 0 0 0 20,18 V6 M4,12 A8,3 0 0 0 20,12",
            ["File"] = "M5,2 H14 L19,7 V22 H5 Z M14,2 V7 H19 M8,11 H16 M8,15 H16 M8,19 H13",
            ["Code"] = "M3,5 H21 V19 H3 Z M9,9 L6,12 L9,15 M15,9 L18,12 L15,15 M13,8 L11,16",
            ["Network"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M3,12 H21 M12,3 C6,8 6,16 12,21 C18,16 18,8 12,3",
            ["Warning"] = "M3,3 H21 V21 H3 Z M12,7 V13 M12,16 V18",
            ["Pause"] = "M8,5 V19 M16,5 V19",
            ["Disabled"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M6,6 L18,18",
            ["Check"] = "M4,12 L9,17 L20,6",
            ["Back"] = "M14,5 L7,12 L14,19 M7,12 H21",
            ["Forward"] = "M9,5 L16,12 L9,19",
            ["Down"] = "M6,9 L12,15 L18,9",
            ["Inbound"] = "M12,3 V19 M6,13 L12,19 L18,13",
            ["Outbound"] = "M12,21 V5 M6,11 L12,5 L18,11",
            ["Export"] = "M12,3 V16 M6,10 L12,16 L18,10 M4,16 V21 H20 V16",
            ["Folder"] = "M3,6 H10 L12,9 H21 V20 H3 Z",
            ["Login"] = "M14,3 H3 V21 H14 M8,12 H21 M17,8 L21,12 L17,16",
            ["Help"] = "M12,2.8 A9.2,9.2 0 1 1 12,21.2 A9.2,9.2 0 1 1 12,2.8 M9.7,9.3 C9.7,7.6 10.7,6.7 12.1,6.7 C13.4,6.7 14.3,7.5 14.3,8.8 C14.3,10.3 12,10.8 12,12.8 L12,13.6 M12,16.8 L12,17.0",
            ["LegendHelp"] = "M9.3,9.2 C9.3,7.1 10.6,5.8 12.2,5.8 C13.8,5.8 15.0,6.8 15.0,8.4 C15.0,10.2 12.0,11.0 12.0,13.3 V14.2 M12.0,17.4 V18.0",
            ["Disk"] = "M3,4 H21 V20 H3 Z M3,15 H21 M16,17.5 H18",
            ["Shield"] = "M12,3 L20,6 V12 C20,16 16.5,19.5 12,21 C7.5,19.5 4,16 4,12 V6 Z M9,12 L11,14 L15,10",
            ["Info"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,11 V17 M12,7 V8.5",
            ["Delete"] = "M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17",
            ["Save"] = "M4,3 H17 L21,7 V21 H4 Z M8,3 V8 H15 V3 M7,13 H17 V21 H7 Z",
            ["Terminal"] = "M3,4 H21 V20 H3 Z M7,9 L10,12 L7,15 M12,15 H17",
            ["Lock"] = "M5,11 H19 V21 H5 Z M8,11 V7 A4,4 0 0 1 16,7 V11 M12,15 V17",
            ["Brand"] = "M2.25,2.25 H21.75 V21.75 H2.25 Z M4.5,8.25 H9 M4.5,12.75 H9 M4.5,17.25 H9 M12.75,9 V17.25 H18.75 V9 M15.75,6 V13.5",
        };
        return paths.ToDictionary(pair => pair.Key, pair =>
        {
            var geometry = Geometry.Parse(pair.Value);
            geometry.Freeze();
            return geometry;
        }, StringComparer.OrdinalIgnoreCase);
    }
}
