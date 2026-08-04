using RelayCove.Client.Sync;

namespace RelayCove.Client.Attachments;

internal sealed class ClientAttachmentFileSelection
{
    internal ClientAttachmentFileSelection(
        Guid draftId,
        ClientAttachmentUploadSource source,
        string displayName,
        string displaySize,
        bool isImage,
        string pathIdentity)
    {
        DraftId = draftId != Guid.Empty
            ? draftId
            : throw new ArgumentException("The attachment draft ID is required.", nameof(draftId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        DisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : throw new ArgumentException("The attachment display name is required.", nameof(displayName));
        DisplaySize = !string.IsNullOrWhiteSpace(displaySize)
            ? displaySize
            : throw new ArgumentException("The attachment display size is required.", nameof(displaySize));
        IsImage = isImage;
        PathIdentity = !string.IsNullOrWhiteSpace(pathIdentity)
            ? pathIdentity
            : throw new ArgumentException("The attachment path identity is required.", nameof(pathIdentity));
    }

    public Guid DraftId { get; }

    public ClientAttachmentUploadSource Source { get; }

    public string DisplayName { get; }

    public string DisplaySize { get; }

    public bool IsImage { get; }

    internal string PathIdentity { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentFileSelection)} {{ DraftId = [REDACTED], " +
        "Source = [REDACTED], DisplayName = [REDACTED], DisplaySize = [REDACTED], " +
        "IsImage = [REDACTED], PathIdentity = [REDACTED] }";
}
