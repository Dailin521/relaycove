namespace RelayCove.Client.Sync;

internal enum ClientAttachmentDownloadHttpStatus
{
    Success,
    AuthenticationRequired,
    AccessRevoked,
    AccessDenied,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    Canceled,
}
