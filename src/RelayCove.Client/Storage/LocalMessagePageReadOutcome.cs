using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalMessagePageReadOutcome(
    LocalCacheOperationStatus Status,
    Guid ConversationId,
    IReadOnlyList<MessageDto> Messages,
    long? NextBeforeMessageId,
    bool HasMoreBefore,
    IReadOnlyList<LocalPendingMessage> PendingMessages)
{
    public LocalMessagePageReadOutcome(
        LocalCacheOperationStatus Status,
        Guid ConversationId,
        IReadOnlyList<MessageDto> Messages,
        long? NextBeforeMessageId,
        bool HasMoreBefore)
        : this(
            Status,
            ConversationId,
            Messages,
            NextBeforeMessageId,
            HasMoreBefore,
            Array.Empty<LocalPendingMessage>())
    {
    }

    public static LocalMessagePageReadOutcome Failure(
        LocalCacheOperationStatus status,
        Guid conversationId) =>
        new(
            status,
            conversationId,
            Array.Empty<MessageDto>(),
            NextBeforeMessageId: null,
            HasMoreBefore: false,
            Array.Empty<LocalPendingMessage>());

    public override string ToString() =>
        $"{nameof(LocalMessagePageReadOutcome)} {{ Status = {Status}, " +
        "ConversationId = [REDACTED], Messages = [REDACTED], " +
        "PendingMessages = [REDACTED], " +
        $"NextBeforeMessageId = [REDACTED], HasMoreBefore = {HasMoreBefore} }}";
}
