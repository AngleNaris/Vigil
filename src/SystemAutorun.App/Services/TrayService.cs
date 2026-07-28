using DrawingIcon = System.Drawing.Icon;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace SystemAutorun.App.Services;

internal sealed class TrayService : IDisposable
{
    private readonly FormsNotifyIcon _notifyIcon;

    internal TrayService(Action show, Action refresh, Action exit)
    {
        var menu = new FormsContextMenuStrip();
        menu.Items.Add(new FormsToolStripMenuItem("打开 SystemAutorun", null, (_, _) => show()));
        menu.Items.Add(new FormsToolStripMenuItem("刷新任务状态", null, (_, _) => refresh()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(new FormsToolStripMenuItem("退出", null, (_, _) => exit()));

        _notifyIcon = new FormsNotifyIcon
        {
            Text = "SystemAutorun",
            Icon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath!),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => show();
    }

    internal void ShowInfo(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
