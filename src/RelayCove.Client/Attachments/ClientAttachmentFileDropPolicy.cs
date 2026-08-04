using RelayCove.Client.Storage;

namespace RelayCove.Client.Attachments;

internal static class ClientAttachmentFileDropPolicy
{
    public static ClientAttachmentFileDropSnapshot Capture(
        bool hasExactFileDrop,
        object? fileDropData,
        int currentAttachmentCount)
    {
        if (!hasExactFileDrop)
        {
            return ClientAttachmentFileDropSnapshot.Failure(
                ClientAttachmentFileDropSnapshotStatus.FileDropFormatNotPresent);
        }

        if (fileDropData is not string[] paths)
        {
            return ClientAttachmentFileDropSnapshot.Failure(
                ClientAttachmentFileDropSnapshotStatus.InvalidFileDropData);
        }

        if (paths.Length == 0)
        {
            return ClientAttachmentFileDropSnapshot.Failure(
                ClientAttachmentFileDropSnapshotStatus.NoFilesSelected);
        }

        if (currentAttachmentCount is < 0 or > ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage ||
            paths.Length > ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage -
                currentAttachmentCount)
        {
            return ClientAttachmentFileDropSnapshot.Failure(
                ClientAttachmentFileDropSnapshotStatus.TooManyFiles);
        }

        return ClientAttachmentFileDropSnapshot.Success(paths);
    }

    public static bool CanShowCopyEffect(
        bool composerCanAccept,
        bool hasExactFileDrop,
        bool sourceAllowsCopy) =>
        composerCanAccept && hasExactFileDrop && sourceAllowsCopy;
}
