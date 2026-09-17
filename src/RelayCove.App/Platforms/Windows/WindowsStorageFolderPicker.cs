using RelayCove.App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsStorageFolderPicker : IStorageFolderPicker
{
    public async Task<string?> PickAsync()
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
            ?? throw new InvalidOperationException("窗口尚未准备好。");
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
