namespace RelayCove.Client.Activation;

internal enum ClientNotificationActivationRouteStatus
{
    Accepted = 1,
    Duplicate = 2,
    NoActiveAccount = 3,
    AccountMismatch = 4,
    AccessDenied = 5,
    NavigationFailed = 6,
    Stopping = 7,
}
