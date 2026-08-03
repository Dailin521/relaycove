namespace RelayCove.Client.Sync;

internal enum ClientMessageHistoryHttpStatus
{
    Success,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
}
