using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalCacheReadOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<MessageDto> Messages);
