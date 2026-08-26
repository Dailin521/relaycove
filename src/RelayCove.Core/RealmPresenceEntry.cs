namespace RelayCove.Core;

public sealed record RealmPresenceEntry(
    string UserEmail,
    DateTimeOffset? ActiveTimestamp,
    DateTimeOffset? IdleTimestamp);
