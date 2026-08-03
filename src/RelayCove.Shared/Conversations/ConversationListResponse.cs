namespace RelayCove.Shared.Conversations;

public sealed record ConversationListResponse(
    IReadOnlyList<ConversationDto> Conversations,
    bool Complete)
{
    public override string ToString() =>
        $"{nameof(ConversationListResponse)} {{ Conversations = [REDACTED], Complete = {Complete} }}";
}
