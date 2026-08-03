using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalCacheMergeOutcome(
    LocalCacheOperationStatus Status,
    IncomingMessageMergeResult? Result);
