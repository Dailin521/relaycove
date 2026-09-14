using System.Security.Cryptography;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Tests;

public sealed class AppUpdateViewModelTests : IDisposable
{
    private static readonly byte[] InstallerBytes = "inert test installer"u8.ToArray();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RichChat-update-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePreferences _preferences = new();
    private readonly FakeService _service = new();
    private readonly FakeFiles _files = new();

    [Fact]
    public async Task Startup_WhenWindowIsHidden_DefersPromptUntilVisibleAndRemembersBuild()
    {
        using var viewModel = Create();
        await viewModel.CheckOnStartupAsync();
        Assert.Equal(1, _service.CheckCount);
        Assert.False(viewModel.IsPromptVisible);
        Assert.Equal(0, _preferences.LastPromptedBuildNumber);

        viewModel.SetWindowActive(true);
        Assert.True(viewModel.IsPromptVisible);
        Assert.Equal(12, _preferences.LastPromptedBuildNumber);
        viewModel.DismissPromptCommand.Execute(null);
        viewModel.SetWindowActive(false);
        viewModel.SetWindowActive(true);
        await viewModel.CheckOnStartupAsync();
        Assert.False(viewModel.IsPromptVisible);
        Assert.Equal(1, _service.CheckCount);

        using var nextLaunch = Create();
        nextLaunch.SetWindowActive(true);
        await nextLaunch.CheckOnStartupAsync();
        Assert.False(nextLaunch.IsPromptVisible);
        Assert.True(nextLaunch.HasUpdate);
    }

    [Fact]
    public async Task Startup_WhenNewerBuildArrives_PromptsAgain()
    {
        _preferences.LastPromptedBuildNumber = 12;
        _service.Result = Update(13);
        using var viewModel = Create();
        viewModel.SetWindowActive(true);
        await viewModel.CheckOnStartupAsync();
        Assert.True(viewModel.IsPromptVisible);
        Assert.Equal(13, _preferences.LastPromptedBuildNumber);
    }

    [Fact]
    public async Task Startup_WhenDisabled_DoesNotCheckButManualCheckWorks()
    {
        _preferences.CheckOnStartup = false;
        using var viewModel = Create();
        viewModel.SetWindowActive(true);
        await viewModel.CheckOnStartupAsync();
        Assert.Equal(0, _service.CheckCount);
        await viewModel.CheckCommand.ExecuteAsync(null);
        Assert.Equal(1, _service.CheckCount);
        Assert.True(viewModel.HasUpdate);
        Assert.False(viewModel.IsPromptVisible);
        Assert.Equal(0, _preferences.LastPromptedBuildNumber);
    }

    [Fact]
    public async Task Check_WhenStartupAndManualOverlap_SharesOneRequest()
    {
        var ready = new TaskCompletionSource<AppUpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Check = _ => ready.Task;
        using var viewModel = Create();
        var startup = viewModel.CheckOnStartupAsync();
        var manual = viewModel.CheckCommand.ExecuteAsync(null);
        Assert.Equal(1, _service.CheckCount);
        Assert.False(viewModel.DownloadCommand.CanExecute(null));
        ready.SetResult(Update());
        await Task.WhenAll(startup, manual);
        Assert.True(viewModel.HasUpdate);
        Assert.False(viewModel.IsChecking);
    }

    [Fact]
    public async Task Startup_WhenDisabledDuringRequest_DoesNotPrompt()
    {
        var ready = new TaskCompletionSource<AppUpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Check = _ => ready.Task;
        using var viewModel = Create();
        viewModel.SetWindowActive(true);
        var startup = viewModel.CheckOnStartupAsync();
        viewModel.CheckOnStartup = false;
        ready.SetResult(Update());
        await startup;
        Assert.False(_preferences.CheckOnStartup);
        Assert.False(viewModel.IsPromptVisible);
        Assert.Equal(0, _preferences.LastPromptedBuildNumber);
    }

    [Fact]
    public async Task Startup_WhenCheckFails_DoesNotPromptOrExposeRawError()
    {
        _service.Check = _ => throw new IOException("private-secret-url");
        using var viewModel = Create();
        viewModel.SetWindowActive(true);
        await viewModel.CheckOnStartupAsync();
        Assert.False(viewModel.IsPromptVisible);
        Assert.False(viewModel.HasUpdate);
        Assert.DoesNotContain("private-secret-url", viewModel.Status);
        Assert.Contains("无法连接", viewModel.Status);
    }

    [Fact]
    public async Task Check_WhenNoEligibleRelease_DoesNotOfferDownload()
    {
        _service.Result = null;
        using var viewModel = Create();
        await viewModel.CheckCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasUpdate);
        Assert.False(viewModel.DownloadCommand.CanExecute(null));
        Assert.Contains("未发现可用更新", viewModel.Status);
    }

    [Fact]
    public async Task Dispose_WhenRequestCompletesLate_DoesNotPresentUpdate()
    {
        var ready = new TaskCompletionSource<AppUpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Check = _ => ready.Task;
        using var viewModel = Create();
        viewModel.SetWindowActive(true);
        var startup = viewModel.CheckOnStartupAsync();
        viewModel.Dispose();
        Assert.True(_service.LastCheckToken.IsCancellationRequested);
        ready.SetResult(Update());
        await startup;
        Assert.False(viewModel.IsPromptVisible);
        Assert.False(viewModel.HasUpdate);
    }

    [Fact]
    public async Task Download_WhenCompleted_WaitsForExplicitOpen()
    {
        using var viewModel = Create();
        await viewModel.CheckCommand.ExecuteAsync(null);
        await viewModel.DownloadCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasDownloadedInstaller);
        Assert.Equal(1, viewModel.DownloadProgress);
        Assert.Equal(0, _files.OpenCount);
        await viewModel.OpenInstallerCommand.ExecuteAsync(null);
        Assert.Equal(1, _files.OpenCount);
        Assert.Equal(_service.SavedPath, _files.OpenedPath);
    }

    [Fact]
    public async Task OpenInstaller_WhenFileWasChanged_RefusesAndAllowsRedownload()
    {
        using var viewModel = Create();
        await viewModel.CheckCommand.ExecuteAsync(null);
        await viewModel.DownloadCommand.ExecuteAsync(null);
        var changed = InstallerBytes.ToArray();
        changed[0] ^= 1;
        await File.WriteAllBytesAsync(_service.SavedPath!, changed);
        await viewModel.OpenInstallerCommand.ExecuteAsync(null);
        Assert.Equal(0, _files.OpenCount);
        Assert.False(viewModel.HasDownloadedInstaller);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Download_WhenCancelled_DoesNotOpenAndCanRetry()
    {
        _service.Download = async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        };
        using var viewModel = Create();
        await viewModel.CheckCommand.ExecuteAsync(null);
        var download = viewModel.DownloadCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDownloading);
        Assert.False(viewModel.CheckCommand.CanExecute(null));
        viewModel.CancelDownloadCommand.Execute(null);
        await download;
        Assert.False(viewModel.HasDownloadedInstaller);
        Assert.False(viewModel.IsDownloading);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
        Assert.Equal(0, _files.OpenCount);
    }

    [Fact]
    public void BuildNumber_WhenReadingAssembly_ComesFromProjectMetadata()
    {
        Assert.True(AppUpdateViewModel.ReadCurrentBuildNumber() > 0);
    }

    private AppUpdateViewModel Create() => new(_service, _preferences, new InlineDispatcher(), _files, _directory, 11);

    private static AppUpdateInfo Update(int buildNumber = 12) => new("1.0.5", buildNumber, "更新说明", DateTimeOffset.UtcNow,
        "RichChat-1.0.5-win-x64-Setup.exe", InstallerBytes.Length, Convert.ToHexString(SHA256.HashData(InstallerBytes)),
        new Uri("https://github.com/Dailin521/relaycove/releases/download/v1.0.5/RichChat-1.0.5-win-x64-Setup.exe"));

    private sealed class FakeService : IAppUpdateService
    {
        public AppUpdateInfo? Result { get; set; } = Update();
        public int CheckCount { get; private set; }
        public CancellationToken LastCheckToken { get; private set; }
        public Func<CancellationToken, Task<AppUpdateInfo?>>? Check { get; set; }
        public Func<CancellationToken, Task<string>>? Download { get; set; }
        public string? SavedPath { get; private set; }
        public Task<AppUpdateInfo?> CheckForUpdateAsync(int currentBuildNumber, CancellationToken cancellationToken = default)
        {
            CheckCount++;
            LastCheckToken = cancellationToken;
            Assert.Equal(11, currentBuildNumber);
            return Check?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }

        public async Task<string> DownloadAsync(AppUpdateInfo update, string destinationDirectory,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Download is not null) return await Download(cancellationToken);
            Directory.CreateDirectory(destinationDirectory);
            SavedPath = Path.Combine(destinationDirectory, update.InstallerName);
            await File.WriteAllBytesAsync(SavedPath, InstallerBytes, cancellationToken);
            progress?.Report(1);
            return SavedPath;
        }
    }

    private sealed class FakePreferences : IAppUpdatePreferences
    {
        public bool CheckOnStartup { get; set; } = true;
        public int LastPromptedBuildNumber { get; set; }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Dispatch(Action action) => action();
        public Task YieldToRenderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeFiles : IFileSaveService
    {
        public int OpenCount { get; private set; }
        public string? OpenedPath { get; private set; }
        public string DownloadFolderPath => throw new NotSupportedException();
        public bool AskWhereToSave { get; set; }
        public bool DownloadedFileExists(string path) => File.Exists(path);
        public Task OpenDownloadedFileAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            OpenedPath = path;
            return Task.CompletedTask;
        }
        public Task<bool> ChooseDownloadFolderAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task OpenDownloadFolderAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShowDownloadedFileInFolderAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadSaveResult> SaveDownloadAsync(string name, Func<Stream, CancellationToken, Task> writeAsync,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
