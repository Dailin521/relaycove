namespace RelayCove.Client.Storage;

internal enum ClientAttachmentOpenStoreStatus
{
    Ready = 1,
    InvalidFileName = 2,
    ValidationFailed = 3,
    QuotaExceeded = 4,
    StoreFull = 5,
    StorageFailure = 6,
    CleanupPending = 7,
}

internal sealed record ClientAttachmentOpenCopyOutcome(
    ClientAttachmentOpenStoreStatus Status,
    ClientAttachmentOpenLease? Lease)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentOpenCopyOutcome)} {{ Status = {Status}, Lease = [REDACTED] }}";
}

internal sealed record ClientAttachmentOpenCleanupOutcome(
    ClientAttachmentOpenStoreStatus Status,
    int DeletedCount,
    int PendingRetryCount)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentOpenCleanupOutcome)} {{ Status = {Status}, " +
        "DeletedCount = [REDACTED], PendingRetryCount = [REDACTED] }";
}

internal sealed record ClientAttachmentOpenRecoveryOutcome(
    ClientAttachmentOpenStoreStatus Status,
    int DeletedCount,
    int PendingRetryCount,
    int ActiveLeaseCount)
{
    public override string ToString() =>
        $"{nameof(ClientAttachmentOpenRecoveryOutcome)} {{ Status = {Status}, " +
        "DeletedCount = [REDACTED], PendingRetryCount = [REDACTED], " +
        "ActiveLeaseCount = [REDACTED] }";
}
