using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Desktop;

internal static class ClientTrayStatusFormatter
{
    internal const int MaximumDisplayedUnreadCount = 999;
    internal const int MaximumToolTipLength = 63;

    public static ClientTrayDisplay Format(ClientTrayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var unread = status.TotalUnreadCount > MaximumDisplayedUnreadCount
            ? $"{MaximumDisplayedUnreadCount}+"
            : status.TotalUnreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var connection = status.ConnectionState switch
        {
            ConnectionState.Disconnected => "Disconnected",
            ConnectionState.Connecting => "Connecting",
            ConnectionState.Connected => "Connected",
            ConnectionState.Reconnecting => "Reconnecting",
            ConnectionState.ServerUnavailable => "Server unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        var toolTip = $"RelayCove | Unread: {unread} | {connection}";
        if (toolTip.Length > MaximumToolTipLength)
        {
            throw new InvalidOperationException("The tray tooltip exceeded the Windows limit.");
        }

        return new ClientTrayDisplay(
            toolTip,
            $"Unread: {unread}",
            $"Status: {connection}");
    }
}
