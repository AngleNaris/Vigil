using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SystemAutorun.App.Controls;

/// <summary>On-demand chart legend; it does not take focus from the invoking button.</summary>
public partial class PlotLegendHelp : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty LegendProperty = DependencyProperty.Register(
        nameof(Legend), typeof(string), typeof(PlotLegendHelp), new PropertyMetadata("Single", LegendChanged));
    public static readonly DependencyProperty AccessibleNameProperty = DependencyProperty.Register(
        nameof(AccessibleName), typeof(string), typeof(PlotLegendHelp), new PropertyMetadata("图例和说明"));
    private static readonly DependencyPropertyKey PrimaryNameKey = DependencyProperty.RegisterReadOnly(
        nameof(PrimaryName), typeof(string), typeof(PlotLegendHelp), new PropertyMetadata(""));
    public static readonly DependencyProperty PrimaryNameProperty = PrimaryNameKey.DependencyProperty;
    private static readonly DependencyPropertyKey SecondaryNameKey = DependencyProperty.RegisterReadOnly(
        nameof(SecondaryName), typeof(string), typeof(PlotLegendHelp), new PropertyMetadata(""));
    public static readonly DependencyProperty SecondaryNameProperty = SecondaryNameKey.DependencyProperty;
    private Window? _owner;

    public string Legend { get => (string)GetValue(LegendProperty); set => SetValue(LegendProperty, value); }
    public string AccessibleName { get => (string)GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public string PrimaryName => (string)GetValue(PrimaryNameProperty);
    public string SecondaryName => (string)GetValue(SecondaryNameProperty);
    internal bool IsOpen => HelpPopup.IsOpen;
    internal System.Windows.Controls.Button Trigger => HelpButton;
    internal System.Windows.Controls.Primitives.Popup Popup => HelpPopup;
    internal FrameworkElement ContentRoot => HelpContent;

    public PlotLegendHelp()
    {
        InitializeComponent();
        HelpContent.DataContext = this;
        Loaded += LoadedOnOwner; Unloaded += UnloadedFromOwner;
        IsVisibleChanged += (_, _) => { if (!IsVisible) { HelpPopup.IsOpen = false; } };
    }

    private static void LegendChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var control = (PlotLegendHelp)target;
        var legend = args.NewValue as string;
        control.SetValue(PrimaryNameKey, legend == "Disk" ? "读取" : legend == "Network" ? "接收" : "");
        control.SetValue(SecondaryNameKey, legend == "Disk" ? "写入" : legend == "Network" ? "发送" : "");
    }

    private void Help_Click(object sender, RoutedEventArgs args) => HelpPopup.IsOpen = !HelpPopup.IsOpen;
    private void Help_PreviewKeyDown(object sender, KeyEventArgs args) => CloseOnEscape(args);
    private void Owner_PreviewKeyDown(object sender, KeyEventArgs args) => CloseOnEscape(args);
    private void CloseOnEscape(KeyEventArgs args)
    {
        if (args.Key != Key.Escape || !HelpPopup.IsOpen) { return; }
        HelpPopup.IsOpen = false; args.Handled = true;
    }
    private void LoadedOnOwner(object sender, RoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, this)) { return; }
        var owner = Window.GetWindow(this);
        if (ReferenceEquals(_owner, owner)) { return; }
        if (_owner is not null) { _owner.PreviewKeyDown -= Owner_PreviewKeyDown; }
        _owner = owner;
        if (_owner is not null) { _owner.PreviewKeyDown += Owner_PreviewKeyDown; }
    }
    private void UnloadedFromOwner(object sender, RoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, this)) { return; }
        HelpPopup.IsOpen = false;
        if (_owner is not null) { _owner.PreviewKeyDown -= Owner_PreviewKeyDown; _owner = null; }
    }
}
