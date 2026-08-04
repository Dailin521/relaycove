namespace RelayCove.Client.Sync;

// This is deliberately a pathless, identity-free presentation result. Details of
// the cache copy and the Attachment Manager HRESULT never cross the coordinator
// boundary into account state or WPF automation.
internal enum ClientAttachmentOpenStatus
{
    HandedToWindows = 1,
    InProgress = 2,
    NotDownloaded = 3,
    AttachmentUnavailable = 4,
    AccessRevoked = 5,
    Stale = 6,
    ValidationFailed = 7,
    InvalidFileName = 8,
    StoreFull = 9,
    PolicyRejected = 10,
    UserCanceled = 11,
    NoAssociation = 12,
    LocalFailure = 13,
    Canceled = 14,
}
