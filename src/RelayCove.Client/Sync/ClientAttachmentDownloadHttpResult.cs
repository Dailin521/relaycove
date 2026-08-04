namespace RelayCove.Client.Sync;

internal sealed record ClientAttachmentDownloadHttpResult(
    ClientAttachmentDownloadHttpStatus Status,
    string? Sha256,
    long? TotalBytes)
{
    public static ClientAttachmentDownloadHttpResult Success(string sha256, long totalBytes)
    {
        if (!IsLowercaseSha256(sha256) || totalBytes <= 0)
        {
            throw new ArgumentException("The download success result is invalid.", nameof(sha256));
        }

        return new(ClientAttachmentDownloadHttpStatus.Success, sha256, totalBytes);
    }

    public static ClientAttachmentDownloadHttpResult Failure(
        ClientAttachmentDownloadHttpStatus status) =>
        new(status, Sha256: null, TotalBytes: null);

    public override string ToString() =>
        $"{nameof(ClientAttachmentDownloadHttpResult)} {{ Status = {Status}, " +
        "Sha256 = [REDACTED], TotalBytes = [REDACTED] }";

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
