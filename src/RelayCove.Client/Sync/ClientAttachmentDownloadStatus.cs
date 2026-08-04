namespace RelayCove.Client.Sync;

internal enum ClientAttachmentDownloadStatus
{
    Completed = 1,
    AlreadyDownloaded = 2,
    InProgress = 3,
    AttachmentUnavailable = 4,
    AuthenticationRequired = 5,
    AccessRevoked = 6,
    AccessDenied = 7,
    Canceled = 8,
    QuotaExceeded = 9,
    TransientFailure = 10,
    ProtocolError = 11,
    RemoteFailure = 12,
    LocalCacheFailure = 13,
}
