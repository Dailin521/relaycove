namespace RelayCove.Core;

public sealed record ChatMessage
{
    public ChatMessage(
        long id,
        ConversationKey conversation,
        long senderId,
        string content,
        DateTimeOffset timestamp,
        bool isRead = false,
        string? senderDisplayName = null)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        ArgumentNullException.ThrowIfNull(conversation);
        if (senderId <= 0) throw new ArgumentOutOfRangeException(nameof(senderId));
        ArgumentNullException.ThrowIfNull(content);
        Id = id;
        Conversation = conversation;
        SenderId = senderId;
        Content = content;
        Timestamp = timestamp;
        IsRead = isRead;
        SenderDisplayName = senderDisplayName;
    }

    public long Id { get; init; }
    public ConversationKey Conversation { get; init; }
    public long SenderId { get; init; }
    public string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool IsRead { get; init; }
    public string? SenderDisplayName { get; init; }

    public override string ToString() =>
        $"ChatMessage {{ Id = {Id}, Conversation = [redacted], SenderId = {SenderId}, Content = [redacted], Timestamp = {Timestamp:O}, IsRead = {IsRead} }}";
}
