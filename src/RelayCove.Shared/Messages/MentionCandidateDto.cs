namespace RelayCove.Shared.Messages;

public sealed record MentionCandidateDto(
    Guid UserId,
    string UserName,
    string DisplayName)
{
    public override string ToString() =>
        $"{nameof(MentionCandidateDto)} {{ UserId = [REDACTED], " +
        "UserName = [REDACTED], DisplayName = [REDACTED] }";
}
