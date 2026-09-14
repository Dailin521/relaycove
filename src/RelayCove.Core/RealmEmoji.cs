namespace RelayCove.Core;

public sealed record RealmEmoji(
    string Id,
    string Name,
    string SourceUrl,
    bool IsDeactivated,
    string? StillUrl = null);
