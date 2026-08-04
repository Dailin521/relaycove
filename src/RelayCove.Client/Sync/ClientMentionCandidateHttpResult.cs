using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientMentionCandidateHttpResult(
    ClientMentionCandidateHttpStatus Status,
    MentionCandidateListResponse? Response)
{
    public static ClientMentionCandidateHttpResult Success(
        MentionCandidateListResponse response) =>
        new(ClientMentionCandidateHttpStatus.Success, response);

    public static ClientMentionCandidateHttpResult Failure(
        ClientMentionCandidateHttpStatus status) =>
        new(status, Response: null);

    public override string ToString() =>
        $"{nameof(ClientMentionCandidateHttpResult)} {{ Status = {Status}, " +
        "Response = [REDACTED] }";
}
