namespace RelayCove.Core;

public sealed class UploadAttachmentRequest
{
    public UploadAttachmentRequest(CredentialEnvelope credentials, AttachmentUpload upload)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(upload);
        Credentials = credentials;
        Upload = upload;
    }

    public CredentialEnvelope Credentials { get; }
    public AttachmentUpload Upload { get; }

    public override string ToString() => "UploadAttachmentRequest { Credentials = [redacted], Upload = [redacted] }";
}
