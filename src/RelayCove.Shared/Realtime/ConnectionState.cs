namespace RelayCove.Shared.Realtime;

public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    ServerUnavailable = 4,
}
