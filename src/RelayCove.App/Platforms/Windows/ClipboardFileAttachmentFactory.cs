using RelayCove.App.Services;
using WinDataPackageView = Windows.ApplicationModel.DataTransfer.DataPackageView;
using Windows.Storage;

namespace RelayCove.App.Platforms.Windows;

internal static class ClipboardFileAttachmentFactory
{
    internal static async Task<IReadOnlyList<SelectedAttachmentFile>> CreateAsync(
        WinDataPackageView dataView, CancellationToken cancellationToken = default)
    {
        var items = await dataView.GetStorageItemsAsync().AsTask(cancellationToken);
        if (items.Count == 0 || items.Any(item => item is not StorageFile))
            throw new InvalidOperationException("The clipboard must contain files only.");
        var selected = new List<SelectedAttachmentFile>(items.Count);
        foreach (var file in items.Cast<StorageFile>())
        {
            var properties = await file.GetBasicPropertiesAsync().AsTask(cancellationToken);
            selected.Add(new SelectedAttachmentFile(
                file.Name, file.ContentType, properties.Size > long.MaxValue ? long.MaxValue : (long)properties.Size,
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    var stream = await file.OpenStreamForReadAsync();
                    if (token.IsCancellationRequested)
                    {
                        await stream.DisposeAsync();
                        token.ThrowIfCancellationRequested();
                    }
                    return stream;
                }, string.IsNullOrWhiteSpace(file.Path) ? null : file.Path));
        }
        return selected;
    }
}
