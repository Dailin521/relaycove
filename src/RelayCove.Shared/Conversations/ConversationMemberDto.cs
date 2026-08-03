namespace RelayCove.Shared.Conversations;

public sealed record ConversationMemberDto(
    Guid UserId,
    string UserName,
    string DisplayName,
    ConversationMemberRole Role,
    DateTimeOffset JoinedAt,
    long LastReadMessageId,
    bool IsMuted);
