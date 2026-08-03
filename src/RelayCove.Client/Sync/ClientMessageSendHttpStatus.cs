namespace RelayCove.Client.Sync;

internal enum ClientMessageSendHttpStatus
{
    Success,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    ValidationFailed,
    IdempotencyConflict,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    Canceled,
}
