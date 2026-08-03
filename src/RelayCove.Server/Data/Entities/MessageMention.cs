namespace RelayCove.Server.Data.Entities;

public sealed class MessageMention
{
    private MessageMention()
    {
    }

    internal MessageMention(Guid mentionedUserId)
    {
        if (mentionedUserId == Guid.Empty)
        {
            throw new ArgumentException("Mentioned user IDs cannot be empty.", nameof(mentionedUserId));
        }

        MentionedUserId = mentionedUserId;
    }

    public long MessageId { get; private set; }

    public Guid MentionedUserId { get; private set; }

    public Message Message { get; private set; } = null!;

    public User MentionedUser { get; private set; } = null!;
}
