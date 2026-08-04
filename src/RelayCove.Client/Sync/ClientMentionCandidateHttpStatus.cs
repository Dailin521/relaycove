namespace RelayCove.Client.Sync;

internal enum ClientMentionCandidateHttpStatus
{
    Success,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
}
