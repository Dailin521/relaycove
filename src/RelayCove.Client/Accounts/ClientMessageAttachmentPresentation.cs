namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageAttachmentPresentation(
    Guid MessageClientId,
    Guid AttachmentId,
    string DisplayName,
    string DisplaySize,
    bool IsImage,
    bool IsDownloaded)
{
    public override string ToString() =>
        $"{nameof(ClientMessageAttachmentPresentation)} {{ MessageClientId = [REDACTED], " +
        "AttachmentId = [REDACTED], " +
        "DisplayName = [REDACTED], DisplaySize = [REDACTED], " +
        $"IsImage = {IsImage}, IsDownloaded = {IsDownloaded} }}";
}
