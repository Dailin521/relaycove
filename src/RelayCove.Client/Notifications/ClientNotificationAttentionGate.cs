namespace RelayCove.Client.Notifications;

internal sealed class ClientNotificationAttentionGate
{
    private int acquired;

    public bool TryAcquire() => Interlocked.Exchange(ref acquired, 1) == 0;
}
