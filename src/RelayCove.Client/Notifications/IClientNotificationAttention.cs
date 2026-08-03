namespace RelayCove.Client.Notifications;

internal interface IClientNotificationAttention
{
    void SignalAcceptedToast();

    void StopFlashing();
}
