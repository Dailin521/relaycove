using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Desktop;

internal sealed record ClientTrayStatus
{
    public ClientTrayStatus(int totalUnreadCount, ConnectionState connectionState)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalUnreadCount);
        if (!Enum.IsDefined(connectionState))
        {
            throw new ArgumentOutOfRangeException(nameof(connectionState));
        }

        TotalUnreadCount = totalUnreadCount;
        ConnectionState = connectionState;
    }

    public int TotalUnreadCount { get; }

    public ConnectionState ConnectionState { get; }

    public override string ToString() =>
        $"{nameof(ClientTrayStatus)} {{ TotalUnreadCount = {TotalUnreadCount}, " +
        $"ConnectionState = {ConnectionState} }}";
}
