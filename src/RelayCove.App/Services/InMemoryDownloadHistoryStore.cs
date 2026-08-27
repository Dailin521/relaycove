using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class InMemoryDownloadHistoryStore : IDownloadHistoryStore
{
    private const int MaximumEntries = 20;
    private readonly Dictionary<AccountId, DownloadHistoryEntry[]> _entries = [];

    public IReadOnlyList<DownloadHistoryEntry> Load(AccountId accountId) =>
        _entries.GetValueOrDefault(accountId) ?? [];

    public void Save(AccountId accountId, IReadOnlyList<DownloadHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries[accountId] = entries
            .OrderByDescending(static entry => entry.CompletedAt)
            .Take(MaximumEntries)
            .ToArray();
    }
}
