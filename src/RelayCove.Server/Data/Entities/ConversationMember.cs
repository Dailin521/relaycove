using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Data.Entities;

public sealed class ConversationMember
{
    private ConversationMember()
    {
    }

    public ConversationMember(
        Guid conversationId,
        Guid userId,
        ConversationMemberRole role,
        DateTime joinedAt,
        long lastReadMessageId = 0,
        bool isMuted = false)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation IDs cannot be empty.", nameof(conversationId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User IDs cannot be empty.", nameof(userId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "Conversation member roles must be defined.");
        }

        if (lastReadMessageId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastReadMessageId), "Last-read message IDs cannot be negative.");
        }

        ConversationId = conversationId;
        UserId = userId;
        Role = role;
        JoinedAt = SqliteValueConverters.NormalizeUtc(joinedAt, nameof(joinedAt));
        LastReadMessageId = lastReadMessageId;
        IsMuted = isMuted;
    }

    public Guid ConversationId { get; private set; }

    public Guid UserId { get; private set; }

    public ConversationMemberRole Role { get; private set; }

    public DateTime JoinedAt { get; private set; }

    public long LastReadMessageId { get; private set; }

    public bool IsMuted { get; private set; }

    public Conversation Conversation { get; private set; } = null!;

    public User User { get; private set; } = null!;

    public void SetRole(ConversationMemberRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "Conversation member roles must be defined.");
        }

        Role = role;
    }

    public void SetMuted(bool isMuted) => IsMuted = isMuted;

    public void AdvanceLastReadMessageId(long messageId)
    {
        if (messageId < LastReadMessageId)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId), "Last-read message IDs cannot move backward.");
        }

        LastReadMessageId = messageId;
    }
}
