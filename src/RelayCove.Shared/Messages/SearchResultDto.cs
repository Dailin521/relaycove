namespace RelayCove.Shared.Messages;

public sealed record SearchResultDto(
    long MessageId,
    Guid ConversationId,
    string ConversationName,
    string SenderName,
    string Snippet,
    DateTimeOffset CreatedAt,
    string? MatchedAttachmentFileName)
{
    public override string ToString() =>
        $"{nameof(SearchResultDto)} {{ [REDACTED] }}";
}
