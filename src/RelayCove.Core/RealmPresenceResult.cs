namespace RelayCove.Core;

public sealed record RealmPresenceResult(
    DateTimeOffset ServerTimestamp,
    IReadOnlyList<RealmPresenceEntry> Presences);
