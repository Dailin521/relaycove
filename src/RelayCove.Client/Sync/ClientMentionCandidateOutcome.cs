using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientMentionCandidateOutcome(
    ClientMentionCandidateStatus Status,
    IReadOnlyList<MentionCandidateDto> Candidates,
    bool HasMore)
{
    public static ClientMentionCandidateOutcome Failure(
        ClientMentionCandidateStatus status) =>
        new(status, Array.Empty<MentionCandidateDto>(), HasMore: false);

    public override string ToString() =>
        $"{nameof(ClientMentionCandidateOutcome)} {{ Status = {Status}, " +
        $"Candidates = [REDACTED], HasMore = {HasMore} }}";
}
