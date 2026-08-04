namespace RelayCove.Client.Updates;

internal enum ClientUpdateDownloadStatus
{
    Success,
    InProgress,
    Canceled,
    TransientFailure,
    ProtocolError,
    StorageFailure,
}
