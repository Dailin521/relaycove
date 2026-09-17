using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed partial class ShellViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentAction_WhenDownloading_BlocksRepeatAndResetsAfterCompletion(bool cancel)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var media = new FakeRealmMediaService
        {
            DownloadWait = async (progress, token) =>
            {
                progress?.Report(new RealmMediaTransferProgress(25, 100));
                await gate.Task.WaitAsync(token);
            }
        };
        using var shell = CreateViewModel(new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7)
        }, realmMediaService: media, fileSaveService: new FakeFileSaveService());
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/guide.pdf");
        var download = shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        Assert.True(attachment.IsDownloading);
        Assert.False(shell.CanStartMediaDownload);
        Assert.Equal("下载中", attachment.DownloadActionText);
        Assert.Equal(0.25, attachment.DownloadProgress);
        Assert.False(attachment.IsDownloadIndeterminate);
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        Assert.Equal(1, media.FileCalls);
        if (cancel) shell.DownloadAttachmentCommand.Cancel();
        else gate.SetResult();
        await download;
        Assert.False(attachment.IsDownloading);
        Assert.True(shell.CanStartMediaDownload);
        Assert.Equal(cancel ? "下载" : "打开", attachment.DownloadActionText);
    }

    [Fact]
    public async Task AttachmentAction_WhenUnknownLengthDownloadFails_StopsAnimationAndAllowsRetry()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var media = new FakeRealmMediaService
        {
            DownloadWait = async (progress, token) =>
            {
                progress?.Report(new RealmMediaTransferProgress(25, null));
                await gate.Task.WaitAsync(token);
                throw new IOException("test failure");
            }
        };
        using var shell = CreateViewModel(new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7)
        }, realmMediaService: media, fileSaveService: new FakeFileSaveService());
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/guide.pdf");
        var download = shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        Assert.True(attachment.IsDownloadIndeterminate);
        Assert.True(attachment.IsDownloading);
        gate.SetResult();
        await download;
        Assert.False(attachment.IsDownloading);
        Assert.False(attachment.IsDownloadIndeterminate);
        Assert.True(shell.CanStartMediaDownload);
        Assert.Equal("下载", attachment.DownloadActionText);
        media.DownloadWait = null;
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        Assert.Equal(2, media.FileCalls);
        Assert.Equal("打开", attachment.DownloadActionText);
    }

    [Fact]
    public void DownloadBorder_WhenTraversed_StartsAtTopAndMovesClockwise()
    {
        var top = RelayCove.App.Controls.DownloadBorderProgress.PointOnOutline(80, 30, 0);
        var right = RelayCove.App.Controls.DownloadBorderProgress.PointOnOutline(80, 30, 0.25);
        var bottom = RelayCove.App.Controls.DownloadBorderProgress.PointOnOutline(80, 30, 0.5);
        var left = RelayCove.App.Controls.DownloadBorderProgress.PointOnOutline(80, 30, 0.75);
        Assert.Equal(40f, top.X, 3);
        Assert.Equal(1f, top.Y, 3);
        Assert.Equal(79f, right.X, 3);
        Assert.Equal(15f, right.Y, 3);
        Assert.Equal(40f, bottom.X, 3);
        Assert.Equal(29f, bottom.Y, 3);
        Assert.Equal(1f, left.X, 3);
        Assert.Equal(15f, left.Y, 3);
    }

    [Fact]
    public async Task AttachmentAction_WhenDownloaded_OpensWithoutDownloadingAgain()
    {
        var media = new FakeRealmMediaService { FileResult = new RealmMediaResult([1], "application/pdf") };
        var save = new FakeFileSaveService();
        var history = new InMemoryDownloadHistoryStore();
        var session = new FakeSession { Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7) };
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/guide.pdf");
        using (var shell = CreateViewModel(session, realmMediaService: media, fileSaveService: save, downloadHistoryStore: history))
        {
            await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
            Assert.Equal("打开", attachment.DownloadActionText);
            await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
            Assert.Single(save.OpenedFiles);
            Assert.Equal(1, media.FileCalls);
        }
        using var restarted = CreateViewModel(session, realmMediaService: media, fileSaveService: save, downloadHistoryStore: history);
        await restarted.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment with { });
        Assert.Equal(2, save.OpenedFiles.Count);
        Assert.Equal(1, media.FileCalls);
    }

    [Fact]
    public async Task AttachmentAction_WhenLocalFileDeleted_DownloadsAgain()
    {
        var media = new FakeRealmMediaService { FileResult = new RealmMediaResult([1], "application/pdf") };
        var save = new FakeFileSaveService();
        using var shell = CreateViewModel(new FakeSession { Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7) }, realmMediaService: media, fileSaveService: save);
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/guide.pdf");
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        save.ExistingFiles.Clear();
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        Assert.Equal(2, media.FileCalls);
        Assert.Empty(save.OpenedFiles);
    }

    [Fact]
    public async Task AttachmentAction_WhenAnotherSourceHasSameName_DoesNotOpenWrongFile()
    {
        var media = new FakeRealmMediaService { FileResult = new RealmMediaResult([1], "application/pdf") };
        var save = new FakeFileSaveService();
        using var shell = CreateViewModel(new FakeSession { Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7) }, realmMediaService: media, fileSaveService: save);
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/a"));
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/b"));
        Assert.Equal(2, media.FileCalls);
        Assert.Empty(save.OpenedFiles);
    }

    [Fact]
    public async Task AttachmentAction_WhenAccountChanges_DoesNotUseAnotherAccountsFile()
    {
        var media = new FakeRealmMediaService { FileResult = new RealmMediaResult([1], "application/pdf") };
        var save = new FakeFileSaveService();
        var history = new InMemoryDownloadHistoryStore();
        var session = new FakeSession { Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7) };
        using var shell = CreateViewModel(session, realmMediaService: media, fileSaveService: save, downloadHistoryStore: history);
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/a");
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 8);
        session.Publish();
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        Assert.Equal(2, media.FileCalls);
        Assert.Empty(save.OpenedFiles);
    }

    [Fact]
    public async Task AttachmentAction_WhenOpenFails_DoesNotDownloadOrThrow()
    {
        var media = new FakeRealmMediaService { FileResult = new RealmMediaResult([1], "application/pdf") };
        var save = new FakeFileSaveService();
        using var shell = CreateViewModel(
            new FakeSession { Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7) },
            realmMediaService: media, fileSaveService: save);
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/a");
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        save.OpenError = new InvalidOperationException();
        await shell.OpenOrDownloadAttachmentCommand.ExecuteAsync(attachment);
        Assert.Equal(1, media.FileCalls);
        Assert.Equal("无法打开文件，请检查关联程序", shell.MediaActionStatus);
    }

    [Fact]
    public void AttachmentAction_WhenOldHistoryHasNoSourceKey_RemainsReadable()
    {
        var entry = System.Text.Json.JsonSerializer.Deserialize<DownloadHistoryEntry>(
            """{"Id":"1bf55991-26fb-4aa2-b4d7-4eaeae4fc3bb","FileName":"old.pdf","FilePath":"C:\\Downloads\\old.pdf","Length":1,"CompletedAt":"2026-09-17T00:00:00Z"}""");
        Assert.NotNull(entry);
        Assert.Null(entry.AttachmentKey);
        Assert.Equal("old.pdf", entry.FileName);
    }

    [Fact]
    public void AttachmentAction_WhenAvailabilityChanges_NotifiesWithoutChangingIdentity()
    {
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/1/a");
        var same = attachment with { };
        var changed = new List<string?>();
        attachment.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        attachment.SetDownloaded(true);
        Assert.Equal("打开", attachment.DownloadActionText);
        Assert.Equal(same, attachment);
        attachment.SetDownloaded(false);
        Assert.Equal("下载", attachment.DownloadActionText);
        Assert.Equal(new[] { "DownloadActionText", "DownloadActionText" }, changed);
    }
}
