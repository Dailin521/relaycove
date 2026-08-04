namespace RelayCove.Client.Sync;

internal enum ClientAttachmentRevealStatus
{
    Revealed = 1,
    NotDownloaded = 2,
    AttachmentUnavailable = 3,
    AccessRevoked = 4,
    Stale = 5,
    ValidationFailed = 6,
    TransientFailure = 7,
    LocalCacheFailure = 8,
    ShellUnavailable = 9,
    Canceled = 10,
}
