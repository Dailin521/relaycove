namespace RelayCove.App.Services;

public interface IFileSaveService
{
    string DownloadFolderPath { get; }
    bool AskWhereToSave { get; set; }
    Task<bool> ChooseDownloadFolderAsync(CancellationToken cancellationToken = default);
    Task OpenDownloadFolderAsync(CancellationToken cancellationToken = default);
    bool DownloadedFileExists(string filePath);
    Task OpenDownloadedFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task ShowDownloadedFileInFolderAsync(string filePath, CancellationToken cancellationToken = default);
    Task<DownloadSaveResult> SaveDownloadAsync(
        string fileName,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default);
}
