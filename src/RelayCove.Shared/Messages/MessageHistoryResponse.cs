namespace RelayCove.Shared.Messages;

public sealed record MessageHistoryResponse(
    IReadOnlyList<MessageDto> Messages,
    long? NextBeforeMessageId,
    bool HasMore);
