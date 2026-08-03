using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record PendingMessage(
    Guid ClientMessageId,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    MessageType Type,
    string? Content,
    long? ReplyToMessageId,
    IReadOnlyList<Guid> MentionUserIds,
    DateTimeOffset CreatedAt);
