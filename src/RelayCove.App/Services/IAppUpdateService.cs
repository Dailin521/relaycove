namespace RelayCove.App.Services;

public interface IAppUpdateService
{
    Task<AppUpdateInfo?> CheckForUpdateAsync(int currentBuildNumber, CancellationToken cancellationToken = default);

    Task<string> DownloadAsync(AppUpdateInfo update, string destinationDirectory,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
