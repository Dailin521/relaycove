namespace RelayCove.Shared.Messages;

public enum SyncReason
{
    Startup = 1,
    Reconnect = 2,
    WindowActivated = 3,
    Periodic = 4,
}
