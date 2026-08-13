using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record ReactionItem(
    long MessageId,
    EmojiReactionIdentity Identity,
    string Display,
    int Count,
    bool CurrentUserReacted,
    string ParticipantLabel)
{
    public string ButtonLabel => $"{Display} {Count}";
    public string AccessibleLabel => $"{Display}，{Count} 人反应；{ParticipantLabel}";
}
