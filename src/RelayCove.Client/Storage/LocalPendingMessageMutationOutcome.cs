namespace RelayCove.Client.Storage;

internal sealed record LocalPendingMessageMutationOutcome(
    LocalCacheOperationStatus Status,
    LocalPendingMessageMutationResult? Result,
    LocalPendingMessage? Message = null)
{
    public static LocalPendingMessageMutationOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Result: null);

    public override string ToString() =>
        $"{nameof(LocalPendingMessageMutationOutcome)} {{ Status = {Status}, " +
        $"Result = {Result}, Message = [REDACTED] }}";
}
