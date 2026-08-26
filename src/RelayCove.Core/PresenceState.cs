namespace RelayCove.Core;

public sealed record PresenceState
{
    public PresenceState(
        bool isAvailable,
        IReadOnlyDictionary<long, UserPresence>? users = null)
    {
        IsAvailable = isAvailable;
        Users = new Dictionary<long, UserPresence>(users ?? new Dictionary<long, UserPresence>());
    }

    public static PresenceState Unavailable { get; } = new(false);
    public bool IsAvailable { get; }
    public IReadOnlyDictionary<long, UserPresence> Users { get; }

    public UserPresenceStatus? ResolveStatus(long userId, DateTimeOffset now)
    {
        if (!IsAvailable) return null;
        return Users.TryGetValue(userId, out var presence)
            ? presence.ResolveStatus(now)
            : UserPresenceStatus.Offline;
    }
}
