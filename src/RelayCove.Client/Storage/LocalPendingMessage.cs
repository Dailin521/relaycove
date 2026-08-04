using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalPendingMessage(
    long LocalId,
    Guid ClientMessageId,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    MessageType Type,
    string? Content,
    long? ReplyToMessageId,
    IReadOnlyList<Guid> MentionUserIds,
    DateTimeOffset CreatedAt,
    MessageSendStatus SendStatus)
{
    public IReadOnlyList<Guid> AttachmentIds { get; init; } = Array.Empty<Guid>();

    public override string ToString() =>
        $"{nameof(LocalPendingMessage)} {{ LocalId = [REDACTED], " +
        "ClientMessageId = [REDACTED], ConversationId = [REDACTED], " +
        "SenderId = [REDACTED], SenderDisplayName = [REDACTED], " +
        $"Type = {Type}, Content = [REDACTED], ReplyToMessageId = [REDACTED], " +
        $"AttachmentIds = [REDACTED], MentionUserIds = [REDACTED], CreatedAt = [REDACTED], SendStatus = {SendStatus} }}";
}
