using RelayCove.App.Services;
using System.Diagnostics;
using Microsoft.Win32;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsFileSaveService : IFileSaveService
{
    internal const string DefaultDownloadFolderName = "RichChat";

    private const string DownloadFolderKey = "relaycove.download.folder";
    private const string AskWhereToSaveKey = "relaycove.download.ask-where";
    private readonly string _defaultDownloadFolder = CreateDefaultDownloadFolderPath();
    private string? _customDownloadFolder;
    private bool _askWhereToSave;

    public WindowsFileSaveService()
    {
        var stored = Preferences.Default.Get(DownloadFolderKey, string.Empty);
        _customDownloadFolder = Path.IsPathFullyQualified(stored) ? stored : null;
        _askWhereToSave = Preferences.Default.Get(AskWhereToSaveKey, false);
    }

    public string DownloadFolderPath => _customDownloadFolder ?? _defaultDownloadFolder;

    public bool AskWhereToSave
    {
        get => _askWhereToSave;
        set
        {
            if (_askWhereToSave == value) return;
            _askWhereToSave = value;
            Preferences.Default.Set(AskWhereToSaveKey, value);
        }
    }

    public async Task<bool> ChooseDownloadFolderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (folder is null) return false;
        _customDownloadFolder = folder.Path;
        Preferences.Default.Set(DownloadFolderKey, _customDownloadFolder);
        return true;
    }

    public Task OpenDownloadFolderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_customDownloadFolder is not null && !Directory.Exists(_customDownloadFolder))
            throw new DirectoryNotFoundException("The configured download folder is unavailable.");
        if (!Directory.Exists(DownloadFolderPath)) Directory.CreateDirectory(DownloadFolderPath);
        Process.Start(new ProcessStartInfo(DownloadFolderPath) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public bool DownloadedFileExists(string filePath) =>
        Path.IsPathFullyQualified(filePath) && File.Exists(filePath);

    public Task OpenDownloadedFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDownloadedFilePath(filePath);
        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task ShowDownloadedFileInFolderAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDownloadedFilePath(filePath);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public async Task<DownloadSaveResult> SaveDownloadAsync(
        string fileName,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(writeAsync);
        cancellationToken.ThrowIfCancellationRequested();
        var sanitized = SanitizeFileName(fileName);
        var askWhereToSave = AskWhereToSave;
        var destinationPath = askWhereToSave
            ? await PickDestinationPathAsync(sanitized, cancellationToken)
            : CreateAutomaticDestinationPath(sanitized);
        if (destinationPath is null) return DownloadSaveResult.Cancelled;

        var directory = Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Download path has no directory.");
        var temporaryPath = Path.Combine(directory, $".relaycove-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writeAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, destinationPath, overwrite: askWhereToSave);
            return new DownloadSaveResult(true, destinationPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    internal static string SanitizeFileName(string fileName)
    {
        var sanitized = new string(Path.GetFileName(fileName)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "attachment.bin" : sanitized;
    }

    internal static string CreateUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < int.MaxValue; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("No available download file name remains.");
    }

    private string CreateAutomaticDestinationPath(string fileName)
    {
        if (_customDownloadFolder is not null && !Directory.Exists(_customDownloadFolder))
            throw new DirectoryNotFoundException("The configured download folder is unavailable.");
        Directory.CreateDirectory(DownloadFolderPath);
        return CreateUniquePath(DownloadFolderPath, fileName);
    }

    private static async Task<string?> PickDestinationPathAsync(string fileName, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension)) extension = ".bin";
        var picker = new FileSavePicker
        {
            SuggestedFileName = string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName))
                ? "attachment"
                : Path.GetFileNameWithoutExtension(fileName)
        };
        picker.FileTypeChoices.Add("附件", [extension]);
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var file = await picker.PickSaveFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file?.Path;
    }

    private static nint GetWindowHandle()
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
            throw new InvalidOperationException("No active window is available.");
        return WindowNative.GetWindowHandle(window);
    }

    private static string CreateDefaultDownloadFolderPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var configured = key?.GetValue(
                "{374DE290-123F-4565-9164-39C4925E467B}",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.Combine(Environment.ExpandEnvironmentVariables(configured), DefaultDownloadFolderName);
        }
        catch
        {
            // Fall back to the conventional per-user Downloads location.
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            DefaultDownloadFolderName);
    }

    private static void ValidateDownloadedFilePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("The downloaded file is unavailable.");
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
