namespace RelayCove.Client.Notifications;

internal enum ClientNotificationDispatchStatus
{
    Completed = 1,
    TransientFailure = 2,
    LocalCacheFailure = 3,
    Canceled = 4,
}
