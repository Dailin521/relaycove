namespace RelayCove.App.Services;

public interface IPlatformInteractionService
{
    Task CopyTextAsync(string text, CancellationToken cancellationToken = default);
    Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default);
}
