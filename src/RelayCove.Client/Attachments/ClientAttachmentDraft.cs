using RelayCove.Client.Storage;
using RelayCove.Client.Sync;

namespace RelayCove.Client.Attachments;

internal sealed class ClientAttachmentDraft
{
    internal ClientAttachmentDraft(
        Guid draftId,
        ClientAttachmentUploadSource source,
        string displayName,
        string displaySize,
        bool isImage,
        string? filePathIdentity,
        long retainedMemoryBytes)
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
        if (filePathIdentity is not null && string.IsNullOrWhiteSpace(filePathIdentity))
        {
            throw new ArgumentException(
                "The file path identity cannot be empty.",
                nameof(filePathIdentity));
        }

        if (retainedMemoryBytes is < 0 or
            > ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedMemoryBytes));
        }

        IsImage = isImage;
        FilePathIdentity = filePathIdentity;
        RetainedMemoryBytes = retainedMemoryBytes;
    }

    public Guid DraftId { get; }

    public ClientAttachmentUploadSource Source { get; }

    public string DisplayName { get; }

    public string DisplaySize { get; }

    public bool IsImage { get; }

    internal string? FilePathIdentity { get; }

    internal long RetainedMemoryBytes { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentDraft)} {{ DraftId = [REDACTED], " +
        "Source = [REDACTED], DisplayName = [REDACTED], DisplaySize = [REDACTED], " +
        "IsImage = [REDACTED], FilePathIdentity = [REDACTED], " +
        "RetainedMemoryBytes = [REDACTED] }";
}
