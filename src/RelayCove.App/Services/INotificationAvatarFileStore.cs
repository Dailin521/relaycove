using RelayCove.Core;

namespace RelayCove.App.Services;

public interface INotificationAvatarFileStore
{
    Task<Uri?> GetAvatarUriAsync(string sourceUrl, CancellationToken cancellationToken = default);
    Task ClearAccountAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
