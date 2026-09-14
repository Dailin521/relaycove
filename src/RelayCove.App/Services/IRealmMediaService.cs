using RelayCove.Core;

namespace RelayCove.App.Services;

public interface IRealmMediaService
{
    event EventHandler<AvatarChangedEventArgs>? AvatarChanged;
    Task<ImageSource> GetImageAsync(string sourceUrl, RealmMediaKind kind, CancellationToken cancellationToken = default);
    Task<RealmMediaResult> GetFileAsync(string sourceUrl, CancellationToken cancellationToken = default);
    Task<RealmMediaDownloadResult> DownloadFileAsync(
        string sourceUrl,
        Stream destination,
        IProgress<RealmMediaTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
