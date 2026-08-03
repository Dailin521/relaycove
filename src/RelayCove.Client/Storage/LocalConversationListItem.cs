using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

internal sealed record LocalConversationListItem(
    Guid Id,
    ConversationType Type,
    string Name,
    string? AvatarUrl,
    long LastMessageId,
    MessageType? LastMessageType,
    string? LastMessageContent,
    DateTimeOffset? LastMessageCreatedAt,
    int UnreadCount,
    bool IsMuted,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() =>
        $"{nameof(LocalConversationListItem)} {{ Id = {Id}, Type = {Type}, " +
        "Name = [REDACTED], AvatarUrl = [REDACTED], " +
        $"LastMessageId = {LastMessageId}, LastMessageType = {LastMessageType}, " +
        "LastMessageContent = [REDACTED], " +
        $"LastMessageCreatedAt = {LastMessageCreatedAt}, UnreadCount = {UnreadCount}, " +
        $"IsMuted = {IsMuted}, UpdatedAt = {UpdatedAt} }}";
}
