namespace RelayCove.App.Platforms.Windows;

internal static class ImageLoadCancellationPolicy
{
    internal static async Task<TImage?> ReturnNullWhenCanceledAsync<TImage>(
        Func<Task<TImage?>> loadAsync,
        CancellationToken cancellationToken)
        where TImage : class
    {
        try
        {
            return await loadAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
