using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalMessagePageReadOutcome(
    LocalCacheOperationStatus Status,
    Guid ConversationId,
    IReadOnlyList<MessageDto> Messages,
    long? NextBeforeMessageId,
    bool HasMoreBefore,
    IReadOnlyList<LocalPendingMessage> PendingMessages,
    long LastReadMessageId = 0,
    int UnreadCount = 0)
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
            Array.Empty<LocalPendingMessage>(),
            LastReadMessageId: 0,
            UnreadCount: 0)
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
            Array.Empty<LocalPendingMessage>(),
            LastReadMessageId: 0,
            UnreadCount: 0);

    public override string ToString() =>
        $"{nameof(LocalMessagePageReadOutcome)} {{ Status = {Status}, " +
        "ConversationId = [REDACTED], Messages = [REDACTED], " +
        "PendingMessages = [REDACTED], " +
        $"NextBeforeMessageId = [REDACTED], HasMoreBefore = {HasMoreBefore}, " +
        "LastReadMessageId = [REDACTED], UnreadCount = [REDACTED] }";
}
