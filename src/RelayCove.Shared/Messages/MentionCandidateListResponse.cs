namespace RelayCove.Shared.Messages;

public sealed record MentionCandidateListResponse(
    Guid ConversationId,
    IReadOnlyList<MentionCandidateDto> Candidates,
    bool HasMore)
{
    public override string ToString() =>
        $"{nameof(MentionCandidateListResponse)} {{ ConversationId = [REDACTED], " +
        $"Candidates = [REDACTED], HasMore = {HasMore} }}";
}
