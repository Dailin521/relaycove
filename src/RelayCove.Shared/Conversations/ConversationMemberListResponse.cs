namespace RelayCove.Shared.Conversations;

public sealed record ConversationMemberListResponse(
    Guid ConversationId,
    IReadOnlyList<ConversationMemberDto> Members);
