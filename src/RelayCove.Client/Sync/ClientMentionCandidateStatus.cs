namespace RelayCove.Client.Sync;

internal enum ClientMentionCandidateStatus
{
    Completed,
    ValidationFailed,
    Canceled,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    LocalCacheFailure,
    Unavailable,
    Stale,
}
