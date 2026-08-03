using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalCacheMergeOutcome(
    LocalCacheOperationStatus Status,
    IncomingMessageMergeResult? Result,
    long? NotificationCandidateMessageId = null)
{
    public override string ToString() =>
        $"{nameof(LocalCacheMergeOutcome)} {{ Status = {Status}, Result = {Result}, " +
        "NotificationCandidateMessageId = [REDACTED] }";
}
