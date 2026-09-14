using RelayCove.App.Platforms.Windows;
using WinDataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using Windows.Storage;

namespace RelayCove.App.Tests;

public sealed class ClipboardFileAttachmentFactoryTests
{
    [Fact]
    public async Task CreateAsync_WhenStorageItemsContainFiles_PreservesNamesAndReadsOriginalBytesLazily()
    {
        var directory = Directory.CreateTempSubdirectory("relaycove-clipboard-");
        var firstPath = Path.Combine(directory.FullName, "资料.txt");
        var secondPath = Path.Combine(directory.FullName, "original.png");
        try
        {
            await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
            await File.WriteAllBytesAsync(secondPath, [4, 5, 6, 7]);
            var package = new WinDataPackage();
            package.SetStorageItems([
                await StorageFile.GetFileFromPathAsync(firstPath),
                await StorageFile.GetFileFromPathAsync(secondPath)]);

            var files = await ClipboardFileAttachmentFactory.CreateAsync(package.GetView());

            Assert.Equal(["资料.txt", "original.png"], files.Select(file => file.FileName));
            Assert.Equal([3L, 4L], files.Select(file => file.Length));
            Assert.Equal("image/png", files[1].ContentType);
            await File.WriteAllBytesAsync(firstPath, [8, 9, 10]);
            await using var stream = await files[0].OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(new byte[] { 8, 9, 10 }, copy.ToArray());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files[1].OpenReadAsync(cancelled.Token));
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
            directory.Delete();
        }
    }

    [Fact]
    public async Task CreateAsync_WhenStorageItemsIncludeFolder_RejectsWholeSelection()
    {
        var directory = Directory.CreateTempSubdirectory("relaycove-clipboard-");
        var filePath = Path.Combine(directory.FullName, "file.txt");
        try
        {
            await File.WriteAllBytesAsync(filePath, [1]);
            var package = new WinDataPackage();
            package.SetStorageItems([
                await StorageFile.GetFileFromPathAsync(filePath),
                await StorageFolder.GetFolderFromPathAsync(directory.FullName)]);

            await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardFileAttachmentFactory.CreateAsync(package.GetView()));
        }
        finally
        {
            File.Delete(filePath);
            directory.Delete();
        }
    }
}
