namespace RelayCove.Shared.Messages;

public sealed record SendMessageRequest(
    Guid ClientMessageId,
    Guid ConversationId,
    MessageType Type,
    string? Content,
    long? ReplyToMessageId,
    IReadOnlyList<Guid> AttachmentIds,
    IReadOnlyList<Guid> MentionUserIds)
{
    public override string ToString() =>
        $"{nameof(SendMessageRequest)} {{ ClientMessageId = {ClientMessageId}, " +
        $"ConversationId = {ConversationId}, Type = {Type}, Content = [REDACTED], " +
        "ReplyToMessageId = [REDACTED], AttachmentIds = [REDACTED], MentionUserIds = [REDACTED] }";
}
