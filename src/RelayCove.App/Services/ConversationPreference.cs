namespace RelayCove.App.Services;

public sealed record ConversationPreference(
    bool IsMuted = false,
    bool IsPinned = false,
    string? Remark = null);
