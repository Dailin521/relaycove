namespace RelayCove.Client.Sync;

internal enum ClientSearchHttpStatus
{
    Success,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    RateLimited,
    Timeout,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
}
