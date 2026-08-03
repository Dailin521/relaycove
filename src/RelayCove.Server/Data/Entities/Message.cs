using System.Buffers;
using System.Globalization;
using System.Text;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Data.Entities;

public sealed class Message
{
    public const int MaximumContentLength = 4_000;
    public const int MaximumMentionCount = 20;

    private Message()
    {
    }

    public Message(
        Guid clientMessageId,
        Guid conversationId,
        Guid senderId,
        MessageType type,
        string? content,
        long? replyToMessageId,
        DateTime createdAt)
    {
        if (clientMessageId == Guid.Empty)
        {
            throw new ArgumentException("Client message IDs cannot be empty.", nameof(clientMessageId));
        }

        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation IDs cannot be empty.", nameof(conversationId));
        }

        if (senderId == Guid.Empty)
        {
            throw new ArgumentException("Sender IDs cannot be empty.", nameof(senderId));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Message types must be defined.");
        }

        if (replyToMessageId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replyToMessageId), "Reply message IDs must be positive.");
        }

        ValidateContent(type, content);
        ClientMessageId = clientMessageId;
        ConversationId = conversationId;
        SenderId = senderId;
        Type = type;
        Content = content;
        ReplyToMessageId = replyToMessageId;
        CreatedAt = SqliteValueConverters.NormalizeUtc(createdAt, nameof(createdAt));
    }

    public long Id { get; private set; }

    public Guid ClientMessageId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid SenderId { get; private set; }

    public MessageType Type { get; private set; }

    public string? Content { get; private set; }

    public long? ReplyToMessageId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public Conversation Conversation { get; private set; } = null!;

    public User Sender { get; private set; } = null!;

    public Message? ReplyToMessage { get; private set; }

    public ICollection<Message> Replies { get; } = new List<Message>();

    public ICollection<MessageMention> Mentions { get; } = new List<MessageMention>();

    public void AddMention(Guid mentionedUserId)
    {
        if (Id != 0)
        {
            throw new InvalidOperationException("Persisted messages cannot be changed.");
        }

        if (mentionedUserId == Guid.Empty)
        {
            throw new ArgumentException("Mentioned user IDs cannot be empty.", nameof(mentionedUserId));
        }

        if (Mentions.Count >= MaximumMentionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mentionedUserId),
                $"Messages cannot contain more than {MaximumMentionCount} mentions.");
        }

        if (Mentions.Any(mention => mention.MentionedUserId == mentionedUserId))
        {
            throw new ArgumentException("Mentioned user IDs cannot be duplicated.", nameof(mentionedUserId));
        }

        Mentions.Add(new MessageMention(mentionedUserId));
    }

    internal static void ValidateContent(MessageType type, string? content)
    {
        if (type is MessageType.Image or MessageType.File && content is null)
        {
            return;
        }

        if (content is null)
        {
            throw new ArgumentNullException(nameof(content), "This message type requires content.");
        }

        var scalarCount = 0;
        var hasNonWhitespace = false;
        var remaining = content.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("Message content must contain valid Unicode.", nameof(content));
            }

            scalarCount++;
            if (scalarCount > MaximumContentLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(content),
                    $"Message content cannot exceed {MaximumContentLength} characters.");
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control && rune.Value is not '\t' and not '\r' and not '\n')
            {
                throw new ArgumentException("Message content contains an unsupported control character.", nameof(content));
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            remaining = remaining[charsConsumed..];
        }

        if (scalarCount == 0 || !hasNonWhitespace)
        {
            throw new ArgumentException("Message content cannot be empty or whitespace.", nameof(content));
        }
    }
}
