using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

internal sealed record LocalNotificationCandidate(
    long MessageId,
    Guid ConversationId,
    ConversationType ConversationType,
    string ConversationName,
    Guid SenderId,
    string SenderDisplayName,
    MessageType MessageType,
    string? Content,
    DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"{nameof(LocalNotificationCandidate)} {{ MessageId = [REDACTED], " +
        "ConversationId = [REDACTED], ConversationType = " +
        $"{ConversationType}, ConversationName = [REDACTED], SenderId = [REDACTED], " +
        $"SenderDisplayName = [REDACTED], MessageType = {MessageType}, " +
        "Content = [REDACTED], CreatedAt = [REDACTED] }";
}
