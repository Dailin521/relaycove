namespace RelayCove.Client.Storage;

public enum LocalCacheOperationStatus
{
    Ready = 1,
    UnknownConversation = 2,
    RevokedConversation = 3,
    FatalScope = 4,
    AuthoritativeSnapshotRequired = 5,
    ProtocolError = 6,
    StaleCursor = 7,
    Conflict = 8,
    TransientFailure = 9,
    NotificationStateNotAdopted = 10,
}
