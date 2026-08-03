namespace RelayCove.Client.Notifications;

internal enum ClientNotificationPlatformStatus
{
    Accepted = 1,
    TransientFailure = 2,
    PermanentlyUnavailable = 3,
}
