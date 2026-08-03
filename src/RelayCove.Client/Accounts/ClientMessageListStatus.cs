namespace RelayCove.Client.Accounts;

internal enum ClientMessageListStatus
{
    None,
    Loading,
    Ready,
    AuthoritativeSnapshotRequired,
    RevokedConversation,
    TransientFailure,
    FatalScope,
    AuthenticationRequired,
    AccessDenied,
    ProtocolError,
    RemoteFailure,
    LocalCacheFailure,
}
