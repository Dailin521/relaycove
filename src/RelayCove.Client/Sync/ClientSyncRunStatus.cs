namespace RelayCove.Client.Sync;

public enum ClientSyncRunStatus
{
    Completed = 1,
    AuthenticationRequired = 2,
    TransientFailure = 3,
    ProtocolError = 4,
    CursorInvalid = 5,
    LocalCacheFailure = 6,
    RemoteFailure = 7,
    Canceled = 8,
}
