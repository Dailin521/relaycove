using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RelayCove.Client.Desktop;

internal sealed class WindowsFormsClientTrayIcon : IClientTrayIcon
{
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip contextMenu;
    private readonly Forms.ToolStripMenuItem unreadItem;
    private readonly Forms.ToolStripMenuItem connectionItem;
    private readonly Forms.ToolStripMenuItem openItem;
    private readonly Forms.ToolStripMenuItem exitItem;
    private bool disposed;

    public WindowsFormsClientTrayIcon()
    {
        unreadItem = new Forms.ToolStripMenuItem { Enabled = false };
        connectionItem = new Forms.ToolStripMenuItem { Enabled = false };
        openItem = new Forms.ToolStripMenuItem("Open RelayCove");
        exitItem = new Forms.ToolStripMenuItem("Exit RelayCove");
        openItem.Click += OnOpenClicked;
        exitItem.Click += OnExitClicked;
        contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.AddRange(
        [
            openItem,
            new Forms.ToolStripSeparator(),
            unreadItem,
            connectionItem,
            new Forms.ToolStripSeparator(),
            exitItem,
        ]);
        notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = Drawing.SystemIcons.Application,
            Visible = false,
        };
        notifyIcon.DoubleClick += OnOpenClicked;
    }

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public void Show(ClientTrayDisplay display)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Update(display);
        notifyIcon.Visible = true;
    }

    public void Update(ClientTrayDisplay display)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(display);
        notifyIcon.Text = display.ToolTipText;
        unreadItem.Text = display.UnreadText;
        connectionItem.Text = display.ConnectionText;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        notifyIcon.DoubleClick -= OnOpenClicked;
        openItem.Click -= OnOpenClicked;
        exitItem.Click -= OnExitClicked;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        contextMenu.Dispose();
    }

    private void OnOpenClicked(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        OpenRequested?.Invoke();
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        ExitRequested?.Invoke();
    }
}
