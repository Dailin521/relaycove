namespace RelayCove.Client.Sync;

internal sealed record ClientAttachmentDownloadOutcome(
    ClientAttachmentDownloadStatus Status,
    string? LocalPath)
{
    internal static ClientAttachmentDownloadOutcome Failure(
        ClientAttachmentDownloadStatus status) =>
        new(status, LocalPath: null);

    public override string ToString() =>
        $"{nameof(ClientAttachmentDownloadOutcome)} {{ Status = {Status}, " +
        "LocalPath = [REDACTED] }";
}
