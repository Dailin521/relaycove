namespace RelayCove.Core;

public sealed record AccountSnapshot
{
    public AccountSnapshot(
        StoredAccount account,
        bool isCacheUnlocked,
        ClientState state,
        IReadOnlyList<ConversationKey>? recentDirectMessages = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(state);
        Account = account;
        IsCacheUnlocked = isCacheUnlocked;
        State = state;
        RecentDirectMessages = (recentDirectMessages ?? []).ToArray();
    }

    public StoredAccount Account { get; }
    public bool IsCacheUnlocked { get; }
    public ClientState State { get; }
    public IReadOnlyList<ConversationKey> RecentDirectMessages { get; }
}
