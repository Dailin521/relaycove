namespace RelayCove.Client.Sync;

internal enum ClientAttachmentUploadHttpStatus
{
    Success,
    AuthenticationRequired,
    ValidationFailed,
    AttachmentTooLarge,
    SourceUnavailable,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    Canceled,
}
