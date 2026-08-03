namespace RelayCove.Shared.Conversations;

public sealed record ConversationDto(
    Guid Id,
    ConversationType Type,
    string Name,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long LastMessageId,
    long LastReadMessageId,
    int UnreadCount,
    bool IsMuted = false);
