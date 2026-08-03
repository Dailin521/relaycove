using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal readonly record struct ClientNotificationRoundToken(
    long Generation,
    SyncReason Reason);
