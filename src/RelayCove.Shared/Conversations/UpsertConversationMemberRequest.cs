namespace RelayCove.Shared.Conversations;

public sealed record UpsertConversationMemberRequest(
    Guid UserId,
    ConversationMemberRole Role);
