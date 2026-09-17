using RelayCove.App.Services;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Tests;

public sealed class StorageLocationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RichChat-storage-tests", Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "old", "Data");
    private string Cache => Path.Combine(root, "old", "Cache");
    private string Parent => Path.Combine(root, "selected");
    private string Target => Path.Combine(Parent, "RichChatData");
    private StorageLocationService Create() => new(Data, Cache);

    public StorageLocationServiceTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Parent);
    }

    [Fact]
    public async Task Prepare_WhenFirstRun_PreservesDefaultPaths()
    {
        var service = Create();
        await service.LoadAsync();
        await service.PrepareAsync();
        Assert.Equal(Data, service.DataPath);
        Assert.Equal(Cache, service.CachePath);
    }

    [Fact]
    public async Task RequestMove_WhenSaved_DoesNotMoveUntilRestart()
    {
        var service = Create();
        await service.RequestMoveAsync(Parent);
        Assert.Equal(Data, service.DataPath);
        Assert.False(Directory.Exists(Target));
        var next = Create();
        await next.LoadAsync();
        Assert.Equal(Target, next.PendingPath);
        await next.PrepareAsync();
        Assert.Equal(Path.Combine(Target, "Data"), next.DataPath);
        Assert.Null(next.PendingPath);
    }

    [Fact]
    public async Task Prepare_WhenMigrating_CopiesAccountsWalFavoritesAndCachesBeforeRemovingOriginals()
    {
        var relativeFiles = new[] { "accounts/user/relaycove.db", "accounts/user/relaycove.db-wal", "accounts/user/relaycove.db-shm",
            "sticker-favorites/favorites/user/library.json", "sticker-favorites/favorites/user/image.gif", "sticker-cache/catalog.json", "updates/setup.exe" };
        foreach (var relative in relativeFiles) Write(Path.Combine(Data, relative), relative);
        Write(Path.Combine(Cache, "notification-avatars/user/avatar.png"), "avatar");
        Write(Path.Combine(Cache, "unrelated.tmp"), "leave");
        Write(Path.Combine(Data, "unrelated.json"), "leave");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        await service.PrepareAsync();
        foreach (var relative in relativeFiles)
        {
            Assert.Equal(relative, File.ReadAllText(Path.Combine(service.DataPath, relative)));
            Assert.False(File.Exists(Path.Combine(Data, relative)));
        }
        Assert.Equal("avatar", File.ReadAllText(Path.Combine(service.CachePath, "notification-avatars/user/avatar.png")));
        Assert.True(File.Exists(Path.Combine(Cache, "unrelated.tmp")));
        Assert.True(File.Exists(Path.Combine(Data, "unrelated.json")));
        Assert.True(File.Exists(Path.Combine(Data, "storage-location.json")));
        var restart = Create();
        await restart.LoadAsync();
        await restart.PrepareAsync();
        Assert.Equal(service.DataPath, restart.DataPath);
    }

    [Fact]
    public async Task CancelMove_WhenPending_LeavesOriginalAndDoesNotCreateTarget()
    {
        var service = Create();
        await service.RequestMoveAsync(Parent);
        await service.CancelMoveAsync();
        var restart = Create();
        await restart.LoadAsync();
        await restart.PrepareAsync();
        Assert.Null(restart.PendingPath);
        Assert.Equal(Data, restart.DataPath);
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task RequestMove_WhenTargetExistsOrOverlaps_RejectsWithoutChangingConfiguration()
    {
        var service = Create();
        await Assert.ThrowsAsync<IOException>(() => service.RequestMoveAsync(Data));
        Directory.CreateDirectory(Target);
        Write(Path.Combine(Target, "user.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => service.RequestMoveAsync(Parent));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Target, "user.txt")));
        Assert.Null(service.PendingPath);
    }

    [Fact]
    public async Task Prepare_WhenTargetBecomesUnavailable_PreservesSourceAndPendingRequest()
    {
        Write(Path.Combine(Data, "accounts/user/relaycove.db"), "database");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        Directory.Delete(Parent);
        await service.PrepareAsync();
        Assert.Equal(Data, service.DataPath);
        Assert.NotNull(service.PendingPath);
        Assert.Contains("未完成", service.Status);
        Assert.True(File.Exists(Path.Combine(Data, "accounts/user/relaycove.db")));
    }

    [Fact]
    public async Task Prepare_WhenActiveDirectoryMissing_DoesNotFallbackOrCreateEmptyStore()
    {
        var service = Create();
        await service.RequestMoveAsync(Parent);
        await service.PrepareAsync();
        Directory.Move(Target, Target + "-offline");
        var restart = Create();
        await restart.LoadAsync();
        await Assert.ThrowsAsync<IOException>(() => restart.PrepareAsync());
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task Prepare_WhenCommitInterrupted_ResumesVerifiedTargetOnRestart()
    {
        Write(Path.Combine(Data, "accounts/user/relaycove.db-wal"), "wal");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        var blocker = Path.Combine(Data, "storage-location.json.tmp");
        Directory.CreateDirectory(blocker);
        await service.PrepareAsync();
        Assert.Equal(Data, service.DataPath);
        Assert.True(File.Exists(Path.Combine(Data, "accounts/user/relaycove.db-wal")));
        Assert.True(File.Exists(Path.Combine(Target, ".migration.json")));
        Directory.Delete(blocker);
        var restart = Create();
        await restart.LoadAsync();
        await restart.PrepareAsync();
        Assert.Equal(Path.Combine(Target, "Data"), restart.DataPath);
        Assert.False(File.Exists(Path.Combine(Data, "accounts/user/relaycove.db-wal")));
    }

    [Fact]
    public async Task Prepare_WhenSourceChangedAfterInterruptedCommit_RefusesStaleSnapshot()
    {
        Write(Path.Combine(Data, "accounts/user/relaycove.db"), "original");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        var blocker = Path.Combine(Data, "storage-location.json.tmp");
        Directory.CreateDirectory(blocker);
        await service.PrepareAsync();
        Directory.Delete(blocker);
        Write(Path.Combine(Data, "accounts/newuser/relaycove.db"), "new");
        var restart = Create();
        await restart.LoadAsync();
        await restart.PrepareAsync();
        Assert.Equal(Data, restart.DataPath);
        Assert.True(File.Exists(Path.Combine(Data, "accounts/newuser/relaycove.db")));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("nullFiles")]
    [InlineData("invalidHash")]
    public async Task Prepare_WhenRecoveryMarkerInvalid_ContinuesOriginalAndAllowsCancel(string invalid)
    {
        Write(Path.Combine(Data, "accounts/user/relaycove.db"), "original");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        var blocker = Path.Combine(Data, "storage-location.json.tmp");
        Directory.CreateDirectory(blocker);
        await service.PrepareAsync();
        Directory.Delete(blocker);
        var markerPath = Path.Combine(Target, ".migration.json");
        var marker = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(markerPath))!;
        if (invalid == "nullFiles") marker["Files"] = null;
        if (invalid == "invalidHash") marker["Files"]![0]!["Hash"] = new string('x', 64);
        File.WriteAllText(markerPath, invalid == "truncated" ? "{" : marker.ToJsonString());
        var restart = Create();
        await restart.LoadAsync();
        await restart.PrepareAsync();
        Assert.Equal(Data, restart.DataPath);
        Assert.NotNull(restart.PendingPath);
        await restart.CancelMoveAsync();
        Assert.Null(restart.PendingPath);
        Assert.True(File.Exists(Path.Combine(Data, "accounts/user/relaycove.db")));
    }

    [Fact]
    public async Task Prepare_WhenCopyWasInterrupted_ReplacesOnlyOwnedStageAndKeepsOriginalUntilComplete()
    {
        Write(Path.Combine(Data, "accounts/u/relaycove.db"), "complete");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Data, "storage-location.json")))!;
        var id = config["MigrationId"]!.GetValue<string>();
        var stage = Path.Combine(Parent, ".RichChat-migration-" + id);
        Write(Path.Combine(stage, ".owner"), id);
        Write(Path.Combine(stage, "Data/accounts/u/relaycove.db"), "partial");
        await service.PrepareAsync();
        Assert.Equal("complete", File.ReadAllText(Path.Combine(service.DataPath, "accounts/u/relaycove.db")));
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public async Task Prepare_WhenCancelledBeforeCopy_DoesNotSwitchOrRemoveOriginal()
    {
        Write(Path.Combine(Data, "accounts/u/relaycove.db"), "complete");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(cancellation.Token));
        Assert.Equal(Data, service.DataPath);
        Assert.Equal("complete", File.ReadAllText(Path.Combine(Data, "accounts/u/relaycove.db")));
    }

    [Fact]
    public async Task Prepare_WhenSecondMigration_MovesFromActiveCustomRoot()
    {
        Write(Path.Combine(Data, "sticker-favorites/favorites/u/f.gif"), "favorite");
        var service = Create();
        await service.RequestMoveAsync(Parent);
        await service.PrepareAsync();
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(second);
        await service.RequestMoveAsync(second);
        await service.PrepareAsync();
        Assert.Equal("favorite", File.ReadAllText(Path.Combine(second, "RichChatData/Data/sticker-favorites/favorites/u/f.gif")));
        Assert.False(File.Exists(Path.Combine(Target, "Data/sticker-favorites/favorites/u/f.gif")));
    }

    [Fact]
    public async Task Change_WhenPickerCancelled_DoesNotScheduleMove()
    {
        var service = Create();
        var viewModel = new StorageSettingsViewModel(service, new CancelledPicker());
        await viewModel.ChangeCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasPending);
        Assert.False(viewModel.IsBusy);
    }

    private sealed class CancelledPicker : IStorageFolderPicker
    {
        public Task<string?> PickAsync() => Task.FromResult<string?>(null);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
