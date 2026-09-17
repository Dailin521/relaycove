using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed partial class ShellViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyActiveImage_WhenInvoked_CopiesBytesAndHandlesClipboardFailure(bool fail)
    {
        var interactions = new FakePlatformInteractionService
        {
            CopyImageError = fail ? new IOException("clipboard unavailable") : null
        };
        var media = new FakeRealmMediaService { FileResult = new([1, 2, 3], "image/png") };
        using var shell = CreateViewModel(new FakeSession(), platformInteractions: interactions, realmMediaService: media);
        shell.ActiveMessageAttachment = new("image", "image.png", "/user_uploads/image.png");
        shell.IsMessageMenuOpen = true;
        await shell.CopyActiveImageCommand.ExecuteAsync(null);
        Assert.False(shell.IsMessageMenuOpen);
        Assert.Null(shell.ActiveMessageAttachment);
        Assert.Empty(interactions.Copied);
        if (fail)
        {
            Assert.Empty(interactions.CopiedImages);
            Assert.Equal("无法复制图片，请重试", shell.MediaActionStatus);
        }
        else
        {
            Assert.Equal(media.FileResult.Content, Assert.Single(interactions.CopiedImages));
            Assert.Null(shell.MediaActionStatus);
        }
    }

    [Fact]
    public async Task CopyActiveImage_WhenAccountChangesDuringFetch_DoesNotWriteClipboard()
    {
        var gate = new TaskCompletionSource<RealmMediaResult>();
        var media = new FakeRealmMediaService { GetFileAction = () => gate.Task };
        var interactions = new FakePlatformInteractionService();
        var session = new FakeSession { Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7) };
        using var shell = CreateViewModel(session, platformInteractions: interactions, realmMediaService: media);
        shell.ActiveMessageAttachment = new("image", "image.png", "/user_uploads/image.png");
        var copying = shell.CopyActiveImageCommand.ExecuteAsync(null);
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 8);
        gate.SetResult(new([1], "image/png"));
        await copying;
        Assert.Empty(interactions.CopiedImages);
    }

    [Fact]
    public async Task CopyActiveImage_WhenFetchFails_ShowsErrorWithoutChangingClipboard()
    {
        var media = new FakeRealmMediaService
        {
            GetFileAction = () => Task.FromException<RealmMediaResult>(new IOException("fetch failed"))
        };
        var interactions = new FakePlatformInteractionService();
        using var shell = CreateViewModel(new FakeSession(), realmMediaService: media, platformInteractions: interactions);
        shell.ActiveMessageAttachment = new("image", "image.png", "/user_uploads/image.png");
        await shell.CopyActiveImageCommand.ExecuteAsync(null);
        Assert.Empty(interactions.CopiedImages);
        Assert.Equal("无法复制图片，请重试", shell.MediaActionStatus);
    }

    [Fact]
    public async Task CopyActiveImage_WhenFileSelected_DoesNotFetchOrCopy()
    {
        var media = new FakeRealmMediaService();
        using var shell = CreateViewModel(new FakeSession(), realmMediaService: media);
        shell.ActiveMessageAttachment = new("file", "file.pdf", "/user_uploads/file.pdf");
        await shell.CopyActiveImageCommand.ExecuteAsync(null);
        Assert.Equal(0, media.FileCalls);
    }
}
