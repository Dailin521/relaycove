namespace RelayCove.Client.Storage;

internal enum LocalPendingMessageMutationResult
{
    Created,
    PreparedRetry,
    MarkedFailed,
    AlreadyExists,
    AlreadySent,
    NotFound,
    NotRetryable,
    CapacityExceeded,
    Conflict,
}
