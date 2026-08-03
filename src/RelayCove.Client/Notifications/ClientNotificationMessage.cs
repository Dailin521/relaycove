using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed record ClientNotificationMessage(
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
        $"{nameof(ClientNotificationMessage)} {{ MessageId = [REDACTED], " +
        "ConversationId = [REDACTED], ConversationType = " +
        $"{ConversationType}, ConversationName = [REDACTED], SenderId = [REDACTED], " +
        $"SenderDisplayName = [REDACTED], MessageType = {MessageType}, " +
        "Content = [REDACTED], CreatedAt = [REDACTED] }";
}
