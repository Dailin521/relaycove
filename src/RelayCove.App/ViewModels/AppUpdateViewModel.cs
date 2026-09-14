using System.Reflection;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;

namespace RelayCove.App.ViewModels;

public sealed partial class AppUpdateViewModel : ObservableObject, IDisposable
{
    private readonly IAppUpdateService _service;
    private readonly IAppUpdatePreferences _preferences;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFileSaveService _files;
    private readonly string _downloadDirectory;
    private readonly int _currentBuildNumber;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _checkTask;
    private CancellationTokenSource? _downloadCancellation;
    private AppUpdateInfo? _availableUpdate;
    private string? _installerPath;
    private bool _started;
    private bool _windowActive;
    private bool _startupPromptPending;
    private bool _disposed;
    private int _lastPromptedBuild;
    private bool _checkOnStartup;

    public AppUpdateViewModel(
        IAppUpdateService service,
        IAppUpdatePreferences preferences,
        IUiDispatcher dispatcher,
        IFileSaveService files,
        string downloadDirectory,
        int currentBuildNumber)
    {
        _service = service;
        _preferences = preferences;
        _dispatcher = dispatcher;
        _files = files;
        _downloadDirectory = downloadDirectory;
        _currentBuildNumber = currentBuildNumber;
        _checkOnStartup = preferences.CheckOnStartup;
        _lastPromptedBuild = preferences.LastPromptedBuildNumber;
    }

    public static int ReadCurrentBuildNumber()
    {
        var value = typeof(AppUpdateViewModel).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "RichChatBuildNumber")?.Value;
        return int.TryParse(value, out var buildNumber) && buildNumber > 0
            ? buildNumber
            : throw new InvalidOperationException("The application build number is unavailable.");
    }

    public bool CheckOnStartup
    {
        get => _checkOnStartup;
        set
        {
            if (_disposed || value == _checkOnStartup) return;
            try
            {
                _preferences.CheckOnStartup = value;
                SetProperty(ref _checkOnStartup, value);
                if (!value)
                {
                    _startupPromptPending = false;
                    IsPromptVisible = false;
                }
            }
            catch (Exception)
            {
                Status = "无法保存更新设置，请重试。";
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "从 GitHub Release 获取正式版更新。";

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial bool IsOpeningInstaller { get; set; }

    [ObservableProperty]
    public partial bool IsPromptVisible { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    public string DownloadProgressText => $"下载进度 {DownloadProgress:P0}";
    public bool HasUpdate => _availableUpdate is not null;
    public string UpdateTitle => _availableUpdate is { } update ? $"发现新版 {update.Version}" : "软件更新";
    public string ReleaseNotes => _availableUpdate?.ReleaseNotes ?? string.Empty;
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
    public bool HasDownloadedInstaller => _installerPath is not null;
    private bool CanCheck => !_disposed && !IsDownloading && !IsOpeningInstaller;
    private bool CanDownload => !_disposed && HasUpdate && !IsChecking && !IsDownloading && !IsOpeningInstaller && !HasDownloadedInstaller;
    private bool CanOpenInstaller => !_disposed && HasDownloadedInstaller && !IsChecking && !IsDownloading && !IsOpeningInstaller;

    public async Task CheckOnStartupAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        if (!CheckOnStartup) return;
        await CheckAsync();
        if (_disposed || !CheckOnStartup) return;
        _startupPromptPending = HasUpdate;
        TryShowStartupPrompt();
    }

    public void SetWindowActive(bool active)
    {
        if (_disposed) return;
        _windowActive = active;
        TryShowStartupPrompt();
    }

    private void TryShowStartupPrompt()
    {
        if (!_windowActive || !_startupPromptPending || !CheckOnStartup ||
            _availableUpdate is not { } update || update.BuildNumber <= _lastPromptedBuild) return;
        _startupPromptPending = false;
        _lastPromptedBuild = update.BuildNumber;
        // Mark only when the visible window can actually present the prompt.
        try { _preferences.LastPromptedBuildNumber = update.BuildNumber; }
        catch (Exception) { Status = "发现新版，但未能保存提醒记录。"; }
        IsPromptVisible = true;
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (!CanCheck) return;
        // Startup and the settings button share the same in-flight request.
        var task = _checkTask ??= CheckCoreAsync();
        try { await task; }
        finally { if (ReferenceEquals(_checkTask, task)) _checkTask = null; }
    }

    private async Task CheckCoreAsync()
    {
        IsChecking = true;
        Status = "正在检查更新…";
        IsPromptVisible = false;
        _availableUpdate = null;
        _installerPath = null;
        NotifyUpdateChanged();
        try
        {
            var update = await _service.CheckForUpdateAsync(_currentBuildNumber, _lifetime.Token);
            if (_disposed) return;
            _availableUpdate = update;
            Status = update is null ? "未发现可用更新。" : $"新版 {update.Version} 已可下载。";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (OperationCanceledException) { Status = "检查更新超时，请重试。"; }
        catch (Exception) { if (!_disposed) Status = "无法连接 GitHub 检查更新，请稍后重试。"; }
        finally
        {
            if (!_disposed)
            {
                IsChecking = false;
                NotifyUpdateChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (!CanDownload || _availableUpdate is not { } update) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCancellation = cancellation;
        IsDownloading = true;
        DownloadProgress = 0;
        Status = "正在下载安装包…";
        try
        {
            var progress = new UpdateProgress(value => _dispatcher.Dispatch(() =>
            {
                if (!_disposed && ReferenceEquals(_downloadCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    DownloadProgress = Math.Clamp(value, 0, 1);
            }));
            var path = await _service.DownloadAsync(update, _downloadDirectory, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            _installerPath = path;
            DownloadProgress = 1;
            Status = "下载完成，校验通过。请先处理未发送的草稿，再打开安装包。";
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) Status = cancellation.IsCancellationRequested ? "下载已取消，可重新下载。" : "下载超时，请重试。";
        }
        catch (Exception)
        {
            if (!_disposed) Status = "下载失败或安装包校验未通过，请重新下载。";
        }
        finally
        {
            _downloadCancellation = null;
            if (!_disposed)
            {
                IsDownloading = false;
                NotifyUpdateChanged();
            }
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCancellation?.Cancel();

    [RelayCommand]
    private void DismissPrompt() => IsPromptVisible = false;

    [RelayCommand(CanExecute = nameof(CanOpenInstaller))]
    private async Task OpenInstallerAsync()
    {
        if (!CanOpenInstaller || _availableUpdate is not { } update || _installerPath is not { } path) return;
        IsOpeningInstaller = true;
        try
        {
            // A completed download may have been replaced or changed on disk.
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                if (file.Length != update.InstallerSize ||
                    !Convert.ToHexString(await SHA256.HashDataAsync(file, _lifetime.Token))
                        .Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _installerPath = null;
                    Status = "安装包已更改或不完整，请重新下载。";
                    return;
                }
            }
            if (_disposed) return;
            await _files.OpenDownloadedFileAsync(path, _lifetime.Token);
            if (!_disposed) Status = "已打开安装包，请按安装向导完成更新。";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception)
        {
            if (!_disposed)
            {
                _installerPath = null;
                Status = "无法打开安装包，请重新下载后重试。";
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsOpeningInstaller = false;
                NotifyUpdateChanged();
            }
        }
    }

    private void NotifyUpdateChanged()
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateTitle));
        OnPropertyChanged(nameof(ReleaseNotes));
        OnPropertyChanged(nameof(HasReleaseNotes));
        OnPropertyChanged(nameof(HasDownloadedInstaller));
        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        OpenInstallerCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCheckingChanged(bool value) => NotifyUpdateChanged();
    partial void OnIsDownloadingChanged(bool value) => NotifyUpdateChanged();
    partial void OnIsOpeningInstallerChanged(bool value) => NotifyUpdateChanged();
    partial void OnDownloadProgressChanged(double value) => OnPropertyChanged(nameof(DownloadProgressText));

    private sealed class UpdateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
