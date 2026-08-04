using System.Text.RegularExpressions;

namespace RelayCove.Client.Storage;

internal enum ClientAttachmentCacheStoreStatus
{
    Ready = 1,
    AlreadyPublished = 2,
    QuotaExceeded = 3,
    NotFound = 4,
    InvalidRelativePath = 5,
    ValidationFailed = 6,
    StorageFailure = 7,
}

internal enum ClientAttachmentCacheStoreEntryKind
{
    Final = 1,
    Staging = 2,
}

internal sealed class ClientAttachmentCacheStoreKey
{
    private static readonly Regex LowercaseSha256 = new(
        "\\A[0-9a-f]{64}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal ClientAttachmentCacheStoreKey(
        Guid conversationId,
        Guid attachmentId,
        string sha256)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (attachmentId == Guid.Empty)
        {
            throw new ArgumentException("Attachment ID must not be empty.", nameof(attachmentId));
        }

        if (sha256 is null || !LowercaseSha256.IsMatch(sha256))
        {
            throw new ArgumentException(
                "SHA-256 must be a lowercase hexadecimal value.",
                nameof(sha256));
        }

        ConversationId = conversationId;
        AttachmentId = attachmentId;
        Sha256 = sha256;
    }

    internal Guid ConversationId { get; }

    internal Guid AttachmentId { get; }

    internal string Sha256 { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreKey)} {{ ConversationId = [REDACTED], " +
        "AttachmentId = [REDACTED], Sha256 = [REDACTED] }";
}

internal sealed record ClientAttachmentCacheStoreStagingOutcome(
    ClientAttachmentCacheStoreStatus Status,
    ClientAttachmentCacheStoreStagingFile? StagingFile)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreStagingOutcome)} {{ Status = {Status}, " +
        "StagingFile = [REDACTED] }";
}

internal sealed record ClientAttachmentCacheStorePublishOutcome(
    ClientAttachmentCacheStoreStatus Status,
    string? RelativePath)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStorePublishOutcome)} {{ Status = {Status}, " +
        "RelativePath = [REDACTED] }";
}

internal sealed record ClientAttachmentCacheStoreValidationOutcome(
    ClientAttachmentCacheStoreStatus Status,
    bool IsValid)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreValidationOutcome)} {{ Status = {Status}, " +
        "IsValid = [REDACTED] }";
}

internal sealed record ClientAttachmentCacheStoreDeleteOutcome(
    ClientAttachmentCacheStoreStatus Status,
    int DeletedCount)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreDeleteOutcome)} {{ Status = {Status}, " +
        "DeletedCount = [REDACTED] }";
}

internal sealed record ClientAttachmentCacheStoreQuotaOutcome(
    ClientAttachmentCacheStoreStatus Status,
    long UsedBytes,
    long QuotaBytes)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreQuotaOutcome)} {{ Status = {Status}, " +
        "UsedBytes = [REDACTED], QuotaBytes = [REDACTED] }";
}

internal sealed record ClientAttachmentCacheStoreEntry(
    ClientAttachmentCacheStoreEntryKind Kind,
    ClientAttachmentCacheStoreKey Key,
    string RelativePath,
    long Length)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreEntry)} {{ Kind = {Kind}, Key = [REDACTED], " +
        "RelativePath = [REDACTED], Length = [REDACTED] }";
}

internal sealed record ClientAttachmentCacheStoreEnumerationOutcome(
    ClientAttachmentCacheStoreStatus Status,
    IReadOnlyList<ClientAttachmentCacheStoreEntry> Entries)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreEnumerationOutcome)} {{ Status = {Status}, " +
        "Entries = [REDACTED] }";
}

internal interface IClientAttachmentCacheStore
{
    Task<ClientAttachmentCacheStoreStagingOutcome> CreateStagingAsync(
        Guid conversationId,
        Guid attachmentId,
        long expectedSize,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentCacheStorePublishOutcome> PublishAsync(
        ClientAttachmentCacheStoreStagingFile stagingFile,
        string verifiedLowercaseSha256,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentCacheStoreValidationOutcome> ValidateAsync(
        string relativePath,
        ClientAttachmentCacheStoreKey expectedKey,
        long expectedSize,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentCacheStoreEnumerationOutcome> EnumerateAsync(
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentCacheStoreDeleteOutcome> DeleteAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentCacheStoreDeleteOutcome> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentCacheStoreQuotaOutcome> GetQuotaAsync(
        CancellationToken cancellationToken = default);
}
