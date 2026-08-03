namespace RelayCove.Shared.Conversations;

public sealed record CreateConversationRequest(
    ConversationType Type,
    string? Name = null,
    Guid? ParticipantUserId = null);
