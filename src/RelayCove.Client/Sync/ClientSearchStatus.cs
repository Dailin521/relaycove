namespace RelayCove.Client.Sync;

internal enum ClientSearchStatus
{
    Completed,
    ValidationFailed,
    Canceled,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    RateLimited,
    Timeout,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    LocalCacheFailure,
    Unavailable,
    Stale,
}
