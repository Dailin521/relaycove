namespace RelayCove.Client.Attachments;

internal sealed class ClientAttachmentFileDropSnapshot
{
    private static readonly string[] NoPaths = [];
    private readonly string[] paths;

    private ClientAttachmentFileDropSnapshot(
        ClientAttachmentFileDropSnapshotStatus status,
        string[] paths)
    {
        Status = status;
        this.paths = paths;
    }

    public ClientAttachmentFileDropSnapshotStatus Status { get; }

    public bool IsSuccess => Status == ClientAttachmentFileDropSnapshotStatus.Success;

    // Return a fresh array so neither the drag-source-owned input nor a caller can mutate the snapshot.
    public string[] Paths => (string[])paths.Clone();

    public static ClientAttachmentFileDropSnapshot Success(string[] paths) =>
        new(ClientAttachmentFileDropSnapshotStatus.Success, (string[])paths.Clone());

    public static ClientAttachmentFileDropSnapshot Failure(
        ClientAttachmentFileDropSnapshotStatus status) =>
        new(status, NoPaths);

    public override string ToString() =>
        $"{nameof(ClientAttachmentFileDropSnapshot)} {{ Status = {Status}, Paths = [REDACTED] }}";
}
