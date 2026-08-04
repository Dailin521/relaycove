namespace RelayCove.Client.Accounts;

internal enum ClientSearchNavigationStatus
{
    Completed,
    Unavailable,
    Stale,
    Canceled,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    LocalCacheFailure,
}
