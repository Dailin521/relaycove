using RelayCove.Shared.Updates;

namespace RelayCove.Client.Updates;

internal interface IClientUpdateDownloader
{
    Task<ClientUpdateDownloadOutcome> DownloadAsync(
        UpdateManifestDto manifest,
        Action<ClientUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    void Cancel();
}
