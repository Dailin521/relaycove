namespace RelayCove.Shared.Messages;

public sealed record MessageDto(
    long Id,
    Guid ClientMessageId,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    MessageType Type,
    string? Content,
    long? ReplyToMessageId,
    IReadOnlyList<AttachmentDto> Attachments,
    IReadOnlyList<Guid> MentionUserIds,
    DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"{nameof(MessageDto)} {{ Id = {Id}, ClientMessageId = {ClientMessageId}, " +
        $"ConversationId = {ConversationId}, SenderId = {SenderId}, Type = {Type}, " +
        "Content = [REDACTED], ReplyToMessageId = [REDACTED], Attachments = [REDACTED], " +
        "MentionUserIds = [REDACTED] }";
}
