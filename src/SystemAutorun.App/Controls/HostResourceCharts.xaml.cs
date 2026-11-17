using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;

namespace SystemAutorun.App.Controls;

public partial class HostResourceCharts : System.Windows.Controls.UserControl
{
    public HostResourceCharts() => InitializeComponent();

    private void ResourceMenu_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { ContextMenu: { } menu } button) { return; }
        menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
}
