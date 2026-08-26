namespace RelayCove.Core;

public sealed record UserPresence
{
    public static readonly TimeSpan DefaultOfflineThreshold = TimeSpan.FromSeconds(200);

    public UserPresence(
        long userId,
        DateTimeOffset? activeTimestamp,
        DateTimeOffset? idleTimestamp)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        UserId = userId;
        ActiveTimestamp = activeTimestamp;
        IdleTimestamp = idleTimestamp;
    }

    public long UserId { get; }
    public DateTimeOffset? ActiveTimestamp { get; }
    public DateTimeOffset? IdleTimestamp { get; }

    public UserPresenceStatus ResolveStatus(
        DateTimeOffset now,
        TimeSpan? offlineThreshold = null)
    {
        var threshold = offlineThreshold ?? DefaultOfflineThreshold;
        if (threshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(offlineThreshold));

        if (IsCurrent(ActiveTimestamp, now, threshold)) return UserPresenceStatus.Active;
        return IsCurrent(IdleTimestamp, now, threshold)
            ? UserPresenceStatus.Idle
            : UserPresenceStatus.Offline;
    }

    private static bool IsCurrent(DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan threshold) =>
        timestamp is { } value && value <= now && now - value <= threshold;
}
