using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed partial class ShellViewModelTests
{
    private static readonly StickerCatalogEntry TestStickerEntry = new("one", "开心猫", "https://zhaoolee.com/ChineseBQB/media/cat.gif",
        "https://zhaoolee.com/ChineseBQB/thumbs/cat.webp", 1, 1, true, 43, "cats", "猫咪");
    private static readonly byte[] StickerGif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    private static StickerPickerItem TestSticker => new(TestStickerEntry, null);

    [Fact]
    public void StickerPanel_WhenOpenedOrReopened_SelectsDefaultWithoutLoadingCatalog()
    {
        var catalog = new FakeStickerCatalog();
        using var stickers = CreateStickers(StickerSession(), catalog);
        stickers.SetOpen(true);
        Assert.True(stickers.IsDefault);
        Assert.Equal(0, catalog.CatalogCalls);
        stickers.SelectTabCommand.Execute("search");
        Assert.True(stickers.IsSearch);
        stickers.SetOpen(false);
        var calls = catalog.CatalogCalls;
        stickers.SetOpen(true);
        Assert.True(stickers.IsDefault);
        Assert.Equal(calls, catalog.CatalogCalls);
        Assert.Empty(stickers.Query);
    }

    [Fact]
    public async Task StickerSend_WhenComposerHasDraftAndAttachment_PreservesBothAndOriginalGif()
    {
        var session = StickerSession();
        var picker = new FakeFileSelectionService { Files = [new("draft.txt", "text/plain", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))] };
        using var stickers = CreateStickers(session);
        using var shell = CreateViewModel(session, fileSelectionService: picker, stickers: stickers);
        shell.ComposerText = "这段文字还没写完";
        await shell.PickAttachmentsCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => shell.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
        var draft = shell.Attachments.Single();
        session.UploadAction = async (upload, token) =>
        {
            using var content = new MemoryStream();
            await upload.Content.CopyToAsync(content, token);
            Assert.Equal(StickerGif, content.ToArray());
            Assert.Equal("image/gif", upload.ContentType);
            return new("开心.gif", "/user_uploads/1/示例/开心.gif");
        };
        shell.IsComposerEmojiPickerOpen = true;

        await stickers.SendCommand.ExecuteAsync(TestSticker);

        Assert.Equal("![开心.gif](/user_uploads/1/示例/开心.gif)", Assert.Single(session.SentContents));
        Assert.Equal("这段文字还没写完", shell.ComposerText);
        Assert.Same(draft, Assert.Single(shell.Attachments));
        Assert.False(shell.IsComposerEmojiPickerOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StickerSend_WhenConversationChangesDuringDownload_DoesNotUploadEvenIfReturning(bool returnToOriginal)
    {
        var session = StickerSession();
        var original = session.Selected;
        var ready = new TaskCompletionSource<StickerMedia>();
        var catalog = new FakeStickerCatalog { MediaAction = _ => ready.Task };
        using var stickers = CreateStickers(session, catalog);
        var send = stickers.SendCommand.ExecuteAsync(TestSticker);
        session.Selected = new DirectMessage([9]);
        session.Publish();
        if (returnToOriginal) { session.Selected = original; session.Publish(); }
        ready.SetResult(StickerImageContent.Validate(StickerGif));
        await send;

        Assert.Equal(0, session.UploadCalls);
        Assert.Empty(session.SentContents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StickerSend_WhenTargetChangesDuringUpload_DoesNotSend(bool changeAccount)
    {
        var session = StickerSession();
        var ready = new TaskCompletionSource<UploadedAttachment>();
        session.UploadAction = (_, _) => ready.Task; // deliberately ignores cancellation
        using var stickers = CreateStickers(session);
        var send = stickers.SendCommand.ExecuteAsync(TestSticker);
        if (changeAccount) session.Account = AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 8);
        else session.Selected = new DirectMessage([9]);
        session.Publish();
        ready.SetResult(new("cat.gif", "/user_uploads/cat.gif"));
        await send;

        Assert.Equal(1, session.UploadCalls);
        Assert.Empty(session.SentContents);
    }

    [Fact]
    public async Task StickerSend_WhenClickedTwiceDuringUpload_UploadsAndSendsOnce()
    {
        var session = StickerSession();
        var ready = new TaskCompletionSource<UploadedAttachment>();
        session.UploadAction = (_, _) => ready.Task;
        using var stickers = CreateStickers(session);
        var first = stickers.SendCommand.ExecuteAsync(TestSticker);
        await stickers.SendCommand.ExecuteAsync(TestSticker);
        ready.SetResult(new("cat.gif", "/user_uploads/cat.gif"));
        await first;
        Assert.Equal(1, session.UploadCalls);
        Assert.Single(session.SentContents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StickerSend_WhenWriteFails_DoesNotAutomaticallyRetry(bool failSend)
    {
        var session = StickerSession();
        if (failSend) session.SendAction = (_, _) => Task.FromException(new IOException("private diagnostics"));
        else session.UploadAction = (_, _) => Task.FromException<UploadedAttachment>(new IOException("private diagnostics"));
        using var stickers = CreateStickers(session);

        await stickers.SendCommand.ExecuteAsync(TestSticker);

        Assert.Equal(1, session.UploadCalls);
        Assert.Equal(failSend ? 1 : 0, session.SentContents.Count);
        Assert.Contains("未确认", stickers.Status);
        Assert.DoesNotContain("private diagnostics", stickers.Status);
        Assert.False(stickers.IsWorking);
    }

    [Fact]
    public async Task StickerSend_WhenRealmLimitIsLower_DoesNotUpload()
    {
        var session = StickerSession();
        var bytes = new byte[checked((int)session.MaxFileUploadBytes + 1)];
        var catalog = new FakeStickerCatalog { MediaAction = _ => Task.FromResult(new StickerMedia(bytes, "image/gif", "cat.gif", "hash")) };
        using var stickers = CreateStickers(session, catalog);
        await stickers.SendCommand.ExecuteAsync(TestSticker);
        Assert.Equal(0, session.UploadCalls);
        Assert.Contains("上传大小", stickers.Status);
    }

    [Fact]
    public async Task StickerSearch_WhenFilteredAndPaged_UsesNameAcrossAllCategoriesWithoutMixingDefaults()
    {
        var catalog = new FakeStickerCatalog { Entries = Enumerable.Range(1, 130)
            .Select(i => TestStickerEntry with { Id = i.ToString(), Label = $"开心猫{i}" }).Append(TestStickerEntry with { Id = "dog", Label = "开心狗", CategoryTitle = "狗狗" }).ToArray() };
        using var stickers = CreateStickers(StickerSession(), catalog);
        stickers.SetOpen(true);
        stickers.SelectTabCommand.Execute("search");
        await WaitUntilAsync(() => stickers.Items.Count == 60);
        Assert.True(stickers.IsSearch);
        Assert.Equal(1, catalog.CatalogCalls);
        Assert.Equal(60, stickers.Items.Count);
        Assert.True(stickers.HasMoreItems);
        stickers.LoadMoreCommand.Execute(null);
        Assert.Equal(120, stickers.Items.Count);
        Assert.True(stickers.HasMoreItems);
        stickers.LoadMoreCommand.Execute(null);
        Assert.False(stickers.HasMoreItems);
        stickers.Query = "狗";
        await stickers.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("开心狗", Assert.Single(stickers.Items).Label);
    }

    [Fact]
    public async Task StickerFavorites_WhenAccountChanges_ClearsOldItemsAndRejectsStaleSelection()
    {
        var session = StickerSession();
        var favorite = new StickerFavorite(new string('a', 64), "个人表情", "a.gif", "image/gif", DateTimeOffset.UnixEpoch);
        var library = new FakeStickerLibrary { Favorites = [favorite] };
        using var stickers = CreateStickers(session, library: library);
        stickers.SetOpen(true);
        stickers.SelectTabCommand.Execute("favorites");
        await stickers.ReloadCommand.ExecuteAsync(null);
        var old = Assert.Single(stickers.Items);
        session.Account = null;
        session.Publish();
        Assert.Empty(stickers.Items);
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 8);
        session.Publish();
        await stickers.SendCommand.ExecuteAsync(old);
        Assert.Equal(0, library.ReadCalls);
        Assert.Equal(0, session.UploadCalls);
    }

    [Fact]
    public async Task StickerSearch_WhenClosedWhilePending_DoesNotRepopulateHiddenImages()
    {
        var ready = new TaskCompletionSource<StickerCatalogSnapshot>();
        using var stickers = CreateStickers(StickerSession(), new FakeStickerCatalog { CatalogAction = _ => ready.Task });
        stickers.SetOpen(true);
        stickers.SelectTabCommand.Execute("search");
        var refresh = stickers.ReloadCommand.ExecuteAsync(null);
        stickers.SetOpen(false);
        ready.SetResult(new([TestStickerEntry], false));
        await refresh;
        Assert.Empty(stickers.Items);
        Assert.False(stickers.IsLoading);
    }

    [Fact]
    public async Task StickerCollect_WhenChatImage_UsesProtectedSessionAndAccountStore()
    {
        var session = StickerSession();
        var catalog = new FakeStickerCatalog();
        var library = new FakeStickerLibrary();
        using var stickers = CreateStickers(session, catalog, library);
        await stickers.CollectMessageImageAsync(new("image", "聊天图片.png", "/user_uploads/私人/图片.png"));
        Assert.Equal(0, catalog.MediaCalls);
        Assert.Equal(session.AccountId!.Value.Value, library.LastAddAccount);
        Assert.Equal("聊天图片", library.LastAddLabel);
        Assert.Contains("已加入", stickers.Status);
    }

    private static FakeSession StickerSession() => new()
    {
        Selected = new DirectMessage([8]),
        StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
    };

    [Theory]
    [InlineData(null, false)]
    [InlineData("正在下载…", true)]
    public async Task CollectActiveImage_WhenSaved_DoesNotCreateOrOverwriteDownloadBanner(string? downloadStatus, bool downloading)
    {
        var session = StickerSession();
        var library = new FakeStickerLibrary();
        using var stickers = CreateStickers(session, library: library);
        using var shell = CreateViewModel(session, stickers: stickers);
        shell.MediaActionStatus = downloadStatus;
        shell.IsMediaActionBusy = downloading;
        shell.ActiveMessageAttachment = new("image", "收藏图片.png", "/user_uploads/image.png");
        shell.IsMessageMenuOpen = true;

        await shell.CollectActiveImageCommand.ExecuteAsync(null);

        Assert.Equal("收藏图片", library.LastAddLabel);
        Assert.False(shell.IsMessageMenuOpen);
        Assert.Equal(downloadStatus, shell.MediaActionStatus);
        Assert.Equal(downloading, shell.IsMediaDownloadStatusVisible);
    }

    [Fact]
    public async Task StickerSearch_WhenTypingAfterOfflineCatalogLoad_FiltersLocallyWithoutAnotherNetworkWait()
    {
        var catalog = new FakeStickerCatalog { CatalogAction = _ => Task.FromResult(new StickerCatalogSnapshot([TestStickerEntry], true)) };
        using var stickers = CreateStickers(StickerSession(), catalog);
        stickers.SetOpen(true);
        stickers.SelectTabCommand.Execute("search");
        stickers.Query = "猫";
        await WaitUntilAsync(() => stickers.Items.Count == 1);
        Assert.Equal(1, catalog.CatalogCalls);
        Assert.Contains("离线", stickers.Status);
    }

    [Theory]
    [InlineData(901, 1000, true, false, true)]
    [InlineData(900, 1000, true, false, false)]
    [InlineData(899, 1000, true, false, false)]
    [InlineData(900, 1000, false, false, false)]
    [InlineData(900, 1000, true, true, false)]
    [InlineData(0, 0, true, false, false)]
    public void StickerPaging_WhenNativeScrollPassesNinetyPercent_LoadsOnlyWhenMoreItemsAreAvailable(
        double verticalOffset,
        double scrollableHeight,
        bool hasMoreItems,
        bool isLoading,
        bool expected) =>
        Assert.Equal(expected, RelayCove.App.Controls.StickerPagingPolicy.ShouldLoadMore(
            verticalOffset,
            scrollableHeight,
            hasMoreItems,
            isLoading));

    [Fact]
    public async Task StickerSend_WhenSessionCancelsAfterSendStarts_ReportsUnknownResultWithoutRetry()
    {
        var session = StickerSession();
        session.SendAction = (_, _) => Task.FromException(new OperationCanceledException());
        using var stickers = CreateStickers(session);
        await stickers.SendCommand.ExecuteAsync(TestSticker);
        Assert.Contains("发送未确认", stickers.Status);
        Assert.Single(session.SentContents);
        Assert.Equal(1, session.UploadCalls);
    }

    [Fact]
    public void StickerPaging_WhenRepeatedEventsStayAboveThreshold_RequestsOnlyOnce()
    {
        var paging = new RelayCove.App.Controls.StickerPagingPolicy();
        Assert.False(paging.TryRequest(900, 1000, true, false));
        Assert.True(paging.TryRequest(901, 1000, true, false));
        Assert.False(paging.TryRequest(950, 1000, true, false));
        Assert.False(paging.TryRequest(1000, 1000, true, false));
    }

    [Fact]
    public void StickerPaging_WhenAppendIncreasesExtent_RearmsWithoutMovingTheViewport()
    {
        var paging = new RelayCove.App.Controls.StickerPagingPolicy();
        Assert.True(paging.TryRequest(950, 1000, true, false));
        Assert.False(paging.TryRequest(950, 2000, true, true));
        Assert.True(paging.TryRequest(1850, 2000, true, false));
        Assert.False(paging.TryRequest(1900, 2000, true, false));
    }

    [Fact]
    public void StickerPaging_WhenBusyOrEmpty_DoesNotConsumeTheNextCrossing()
    {
        var paging = new RelayCove.App.Controls.StickerPagingPolicy();
        Assert.False(paging.TryRequest(950, 1000, true, true));
        Assert.False(paging.TryRequest(950, 1000, false, false));
        Assert.True(paging.TryRequest(950, 1000, true, false));
        paging.Reset();
        Assert.True(paging.TryRequest(950, 1000, true, false));
    }

    private static StickerPickerViewModel CreateStickers(FakeSession session, FakeStickerCatalog? catalog = null,
        FakeStickerLibrary? library = null) => new(session, catalog ?? new(), library ?? new(), new FakeFileSelectionService(), new InlineDispatcher());

    private sealed class FakeStickerCatalog : IStickerCatalogService
    {
        public IReadOnlyList<StickerCatalogEntry> Entries { get; set; } = [TestStickerEntry];
        public Func<CancellationToken, Task<StickerMedia>>? MediaAction { get; set; }
        public Func<CancellationToken, Task<StickerCatalogSnapshot>>? CatalogAction { get; set; }
        public int MediaCalls { get; private set; }
        public int CatalogCalls { get; private set; }
        public Task<StickerCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
        {
            CatalogCalls++;
            return CatalogAction?.Invoke(cancellationToken) ?? Task.FromResult(new StickerCatalogSnapshot(Entries, false));
        }
        public Task<StickerMedia> GetMediaAsync(StickerCatalogEntry entry, bool thumbnail = false, CancellationToken cancellationToken = default)
        {
            MediaCalls++;
            return MediaAction?.Invoke(cancellationToken) ?? Task.FromResult(StickerImageContent.Validate(StickerGif));
        }
    }

    private sealed class FakeStickerLibrary : IStickerLibraryStore
    {
        public IReadOnlyList<StickerFavorite> Favorites { get; set; } = [];
        public string? LastAddAccount { get; private set; }
        public string? LastAddLabel { get; private set; }
        public int ReadCalls { get; private set; }
        public Task<IReadOnlyList<StickerFavorite>> ListAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(Favorites);
        public Task<StickerFavorite> AddAsync(string accountId, Stream content, string label, CancellationToken cancellationToken = default)
        {
            LastAddAccount = accountId;
            LastAddLabel = label;
            return Task.FromResult(new StickerFavorite("hash", label, "cat.gif", "image/gif", DateTimeOffset.UnixEpoch));
        }
        public Task<StickerMedia> ReadAsync(string accountId, string hash, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(StickerImageContent.Validate(StickerGif));
        }
        public Task RenameAsync(string accountId, string hash, string label, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, string hash, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
