using RelayCove.App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsFileSaveService : IFileSaveService
{
    public async Task<bool> SaveAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
        {
            throw new InvalidOperationException("No active window is available.");
        }
        var sanitized = new string(Path.GetFileName(fileName)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray());
        var extension = Path.GetExtension(sanitized);
        if (string.IsNullOrEmpty(extension)) extension = ".bin";
        var picker = new FileSavePicker
        {
            SuggestedFileName = string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(sanitized))
                ? "attachment"
                : Path.GetFileNameWithoutExtension(sanitized)
        };
        picker.FileTypeChoices.Add("附件", [extension]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;
        await using var destination = await file.OpenStreamForWriteAsync();
        destination.SetLength(0);
        await destination.WriteAsync(content, cancellationToken);
        return true;
    }
}
