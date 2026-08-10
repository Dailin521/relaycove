namespace RelayCove.Core;

public enum ConnectionStatus
{
    SignedOut,
    Locked,
    Offline,
    Connecting,
    Connected,
    Reconnecting,
    RateLimited,
    ReauthRequired,
    Faulted
}
