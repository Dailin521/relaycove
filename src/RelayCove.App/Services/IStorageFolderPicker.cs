namespace RelayCove.App.Services;

public interface IStorageFolderPicker
{
    Task<string?> PickAsync();
}
