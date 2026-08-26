using System.Text;

namespace RelayCove.Core;

public sealed record UserStatusContent
{
    public UserStatusContent(string? statusText = null, EmojiReactionIdentity? emoji = null)
    {
        StatusText = statusText?.Trim() ?? string.Empty;
        if (StatusText.EnumerateRunes().Take(61).Count() > 60)
            throw new ArgumentOutOfRangeException(nameof(statusText));
        Emoji = emoji;
    }

    public string StatusText { get; }
    public EmojiReactionIdentity? Emoji { get; }
    public bool IsEmpty => StatusText.Length == 0 && Emoji is null;
}
