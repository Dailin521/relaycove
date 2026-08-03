using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed record ClientNotificationRequest(
    string AccountScopeId,
    NotificationPolicy Policy,
    IReadOnlyList<ClientNotificationMessage> Messages)
{
    public override string ToString() =>
        $"{nameof(ClientNotificationRequest)} {{ AccountScopeId = [REDACTED], " +
        $"Policy = {Policy}, " +
        "Messages = [REDACTED] }";
}
