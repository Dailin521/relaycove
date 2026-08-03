namespace RelayCove.Client.Sync;

internal enum ClientMessageSendStatus
{
    Completed,
    ValidationFailed,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    IdempotencyConflict,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    LocalCacheFailure,
    CapacityExceeded,
    NotRetryable,
    Unavailable,
    Canceled,
}
