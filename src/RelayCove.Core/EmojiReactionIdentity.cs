namespace RelayCove.Core;

public sealed record EmojiReactionIdentity
{
    public EmojiReactionIdentity(string emojiName, string emojiCode, string reactionType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emojiName);
        ArgumentException.ThrowIfNullOrWhiteSpace(emojiCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(reactionType);
        EmojiName = emojiName;
        EmojiCode = emojiCode;
        ReactionType = reactionType;
    }

    public string EmojiName { get; }
    public string EmojiCode { get; }
    public string ReactionType { get; }
    public string CanonicalKey => $"{ReactionType}:{EmojiCode}";
}
