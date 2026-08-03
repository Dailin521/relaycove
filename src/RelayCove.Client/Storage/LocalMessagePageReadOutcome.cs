using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalMessagePageReadOutcome(
    LocalCacheOperationStatus Status,
    Guid ConversationId,
    IReadOnlyList<MessageDto> Messages,
    long? NextBeforeMessageId,
    bool HasMoreBefore)
{
    public static LocalMessagePageReadOutcome Failure(
        LocalCacheOperationStatus status,
        Guid conversationId) =>
        new(
            status,
            conversationId,
            Array.Empty<MessageDto>(),
            NextBeforeMessageId: null,
            HasMoreBefore: false);

    public override string ToString() =>
        $"{nameof(LocalMessagePageReadOutcome)} {{ Status = {Status}, " +
        "ConversationId = [REDACTED], Messages = [REDACTED], " +
        $"NextBeforeMessageId = [REDACTED], HasMoreBefore = {HasMoreBefore} }}";
}
