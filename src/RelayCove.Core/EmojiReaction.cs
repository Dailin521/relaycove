namespace RelayCove.Core;

public sealed record EmojiReaction
{
    public EmojiReaction(EmojiReactionIdentity identity, long userId, string? userFullName = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        Identity = identity;
        UserId = userId;
        UserFullName = string.IsNullOrWhiteSpace(userFullName) ? null : userFullName;
    }

    public EmojiReactionIdentity Identity { get; }
    public long UserId { get; }
    public string? UserFullName { get; }
}
