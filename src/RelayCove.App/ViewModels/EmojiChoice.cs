using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record EmojiChoice(
    string Emoji,
    string Label,
    string EmojiName,
    string EmojiCode,
    string ReactionType = "unicode_emoji")
{
    public EmojiReactionIdentity Identity => new(EmojiName, EmojiCode, ReactionType);
    public string AccessibleLabel => $"{Label} {Emoji}";
}
