using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;

namespace RelayCove.App.ViewModels;

public sealed partial class StorageSettingsViewModel : ObservableObject
{
    private readonly StorageLocationService storage;
    private readonly IStorageFolderPicker picker;

    public StorageSettingsViewModel(StorageLocationService storage, IStorageFolderPicker picker)
    {
        this.storage = storage;
        this.picker = picker;
        Status = storage.Status;
    }

    public string CurrentPath => storage.DisplayPath;
    public string PendingText => storage.PendingPath is { } path ? $"重启后迁移到：{path}" : string.Empty;
    public bool HasPending => storage.PendingPath is not null;
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    [RelayCommand]
    private async Task ChangeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var selected = await picker.PickAsync();
            if (selected is null) return;
            await storage.RequestMoveAsync(selected);
            Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Status = "无法设置缓存目录。请选择有写入权限、空间充足且不含 RichChatData 的本地文件夹，不能选择现有缓存目录内部。";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await storage.CancelMoveAsync(); Refresh(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Status = "取消更改失败，请稍后重试。"; }
        finally { IsBusy = false; }
    }

    private void Refresh()
    {
        Status = storage.Status;
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
    }
}
