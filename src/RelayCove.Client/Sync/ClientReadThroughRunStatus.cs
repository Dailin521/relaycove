namespace RelayCove.Client.Sync;

internal enum ClientReadThroughRunStatus
{
    Completed = 1,
    AuthenticationRequired = 2,
    TransientFailure = 3,
    ProtocolError = 4,
    AccessDenied = 5,
    LocalCacheFailure = 6,
    RemoteFailure = 7,
    Canceled = 8,
}
