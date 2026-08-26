namespace RelayCove.Core;

public sealed record UserCustomStatus
{
    public UserCustomStatus(long userId, UserStatusContent content)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        UserId = userId;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (content.IsEmpty) throw new ArgumentException("A stored user status cannot be empty.", nameof(content));
    }

    public long UserId { get; }
    public UserStatusContent Content { get; }
}
