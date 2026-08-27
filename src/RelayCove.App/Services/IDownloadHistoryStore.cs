using RelayCove.Core;

namespace RelayCove.App.Services;

public interface IDownloadHistoryStore
{
    IReadOnlyList<DownloadHistoryEntry> Load(AccountId accountId);
    void Save(AccountId accountId, IReadOnlyList<DownloadHistoryEntry> entries);
}
