namespace RelayCove.Client.Notifications;

internal sealed class NoOpClientNotificationAttention : IClientNotificationAttention
{
    public static NoOpClientNotificationAttention Instance { get; } = new();

    private NoOpClientNotificationAttention()
    {
    }

    public void SignalAcceptedToast()
    {
    }

    public void StopFlashing()
    {
    }
}
