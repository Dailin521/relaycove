namespace RelayCove.App.Services;

public sealed class MauiPlatformInteractionService : IPlatformInteractionService
{
    public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        await Clipboard.Default.SetTextAsync(text);
    }

    public async Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Launcher.Default.OpenAsync(uri))
        {
            throw new InvalidOperationException("The system could not open the message link.");
        }
    }
}
