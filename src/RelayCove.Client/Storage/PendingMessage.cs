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
    DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"{nameof(PendingMessage)} {{ ClientMessageId = [REDACTED], " +
        "ConversationId = [REDACTED], SenderId = [REDACTED], " +
        $"Type = {Type}, " +
        "SenderDisplayName = [REDACTED], Content = [REDACTED], " +
        "ReplyToMessageId = [REDACTED], MentionUserIds = [REDACTED] }";
}
