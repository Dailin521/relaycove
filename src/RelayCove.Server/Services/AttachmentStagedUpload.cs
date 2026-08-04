namespace RelayCove.Server.Services;

public sealed class AttachmentStagedUpload : IAsyncDisposable
{
    private readonly ILogger logger;
    private string ownedPath;
    private bool preserveOwnedPath;

    internal AttachmentStagedUpload(
        Guid id,
        string originalFileName,
        string contentType,
        string storedFileName,
        string stagingPath,
        string finalPath,
        ILogger logger)
    {
        Id = id;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        StoredFileName = storedFileName;
        StagingPath = stagingPath;
        FinalPath = finalPath;
        ownedPath = stagingPath;
        this.logger = logger;
    }

    public Guid Id { get; }

    public string OriginalFileName { get; }

    public string ContentType { get; }

    public string StoredFileName { get; }

    public long Size { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    internal string StagingPath { get; }

    internal string FinalPath { get; }

    internal void Complete(long size, string sha256)
    {
        if (Size != 0 || string.IsNullOrEmpty(sha256))
        {
            throw new InvalidOperationException("The staged upload is already complete or invalid.");
        }

        Size = size;
        Sha256 = sha256;
    }

    internal void Publish()
    {
        if (Size <= 0 || string.IsNullOrEmpty(Sha256))
        {
            throw new InvalidOperationException("Incomplete uploads cannot be published.");
        }

        File.Move(StagingPath, FinalPath, overwrite: false);
        ownedPath = FinalPath;
    }

    internal void PreservePublishedFile() => preserveOwnedPath = true;

    internal void Accept()
    {
        preserveOwnedPath = true;
        ownedPath = string.Empty;
    }

    public ValueTask DisposeAsync()
    {
        if (string.IsNullOrEmpty(ownedPath) || preserveOwnedPath)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            File.Delete(ownedPath);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to clean an attachment upload artifact for {AttachmentId}.",
                Id);
        }

        return ValueTask.CompletedTask;
    }

    public override string ToString() =>
        $"{nameof(AttachmentStagedUpload)} {{ Id = [REDACTED], OriginalFileName = [REDACTED], " +
        "ContentType = [REDACTED], StoredFileName = [REDACTED], Size = [REDACTED], " +
        "Sha256 = [REDACTED], Paths = [REDACTED] }";
}
