using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed record ClientNotificationRequest(
    NotificationPolicy Policy,
    IReadOnlyList<ClientNotificationMessage> Messages)
{
    public override string ToString() =>
        $"{nameof(ClientNotificationRequest)} {{ Policy = {Policy}, " +
        "Messages = [REDACTED] }";
}
