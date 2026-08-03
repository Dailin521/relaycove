namespace RelayCove.Client.Sync;

internal enum ClientMessageLoadStatus
{
    Completed,
    Canceled,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    LocalCacheFailure,
}
