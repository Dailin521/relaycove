using RelayCove.Core;

namespace RelayCove.App.Services;

public interface INotificationAvatarFileStore
{
    event EventHandler<AvatarChangedEventArgs>? AvatarChanged;
    Task<Uri?> GetAvatarUriAsync(string sourceUrl, CancellationToken cancellationToken = default);
    Task ClearAccountAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
