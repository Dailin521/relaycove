using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RelayCove.Client.Desktop;

internal sealed class WindowsFormsClientTrayIcon : IClientTrayIcon
{
    private readonly Drawing.Icon applicationIcon;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip contextMenu;
    private readonly Forms.ToolStripMenuItem unreadItem;
    private readonly Forms.ToolStripMenuItem connectionItem;
    private readonly Forms.ToolStripMenuItem openItem;
    private readonly Forms.ToolStripMenuItem exitItem;
    private bool disposed;

    public WindowsFormsClientTrayIcon()
    {
        applicationIcon = LoadApplicationIcon();
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
            Icon = applicationIcon,
            Visible = false,
        };
        notifyIcon.DoubleClick += OnOpenClicked;
        notifyIcon.BalloonTipClicked += OnOpenClicked;
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

    public void ShowNotification(string title, string message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        notifyIcon.ShowBalloonTip(
            timeout: 5000,
            tipTitle: title,
            tipText: message,
            tipIcon: Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        notifyIcon.DoubleClick -= OnOpenClicked;
        notifyIcon.BalloonTipClicked -= OnOpenClicked;
        openItem.Click -= OnOpenClicked;
        exitItem.Click -= OnExitClicked;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        applicationIcon.Dispose();
        contextMenu.Dispose();
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                System.IO.IOException or
                UnauthorizedAccessException or
                System.Runtime.InteropServices.ExternalException)
            {
                // A cosmetic icon failure must not prevent the client from starting.
            }
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
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
