namespace RelayCove.Shared.Conversations;

public sealed record ConversationListResponse(
    IReadOnlyList<ConversationDto> Conversations,
    bool Complete);
