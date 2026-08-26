namespace RelayCove.Core;

public sealed record UserStatusState
{
    public UserStatusState(
        bool isAvailable,
        IReadOnlyDictionary<long, UserStatusContent>? users = null)
    {
        IsAvailable = isAvailable;
        Users = new Dictionary<long, UserStatusContent>(users ?? new Dictionary<long, UserStatusContent>());
    }

    public static UserStatusState Unavailable { get; } = new(false);
    public bool IsAvailable { get; }
    public IReadOnlyDictionary<long, UserStatusContent> Users { get; }
}
