using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class AvatarCacheTests
{
    private const string Url = "/user_avatars/1/avatar.png";
    private static readonly AccountId Account = AccountId.Create(RealmEndpoint.Parse("https://avatar.example.test"), 7);

    [Fact]
    public async Task GetAsync_WhenColdReadFailsThenReconnects_NotifiesWaitingViewsOfFirstImage()
    {
        var session = new AvatarSession { Account = Account, Read = _ => Task.FromException<RealmMediaResult>(new IOException()) };
        using var cache = new AvatarCache(session, CreateRoot());
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(Url));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.AvatarChanged += (_, _) => changed.TrySetResult();
        session.Read = _ => Task.FromResult(new RealmMediaResult([2], "image/png"));
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Connected) };
        session.Publish();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 2 }, (await cache.GetAsync(Url)).Media.Content);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("changed")]
    [InlineData("failed")]
    public async Task GetAsync_WhenManyRefreshesComplete_BoundsCacheForEveryOutcome(string outcome)
    {
        var root = CreateRoot();
        var response = new TaskCompletionSource<RealmMediaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AvatarSession { Account = Account, Read = _ => response.Task };
        using var cache = new AvatarCache(session, root);
        var completions = new List<Task>();
        for (var index = 0; index < 80; index++)
        {
            var url = $"/user_avatars/1/{index}.png";
            await SeedAsync(root, Account, [1], url);
            await cache.GetAsync(url);
            completions.Add(cache.WaitForRefreshAsync(url));
        }
        if (outcome == "failed") response.SetException(new IOException());
        else response.SetResult(new RealmMediaResult(outcome == "same" ? [1] : [2], "image/png"));
        try { await Task.WhenAll(completions); }
        catch (IOException) when (outcome == "failed") { }
        Assert.InRange(cache.EntryCount, 1, 64);
    }

    [Fact]
    public async Task GetAsync_WhenCached_ReturnsBeforeBackgroundRefreshAndPublishesChangedBytes()
    {
        var root = CreateRoot();
        var path = await SeedAsync(root, Account, [1, 2]);
        var response = new TaskCompletionSource<RealmMediaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AvatarSession { Account = Account, Read = _ => response.Task };
        using var cache = new AvatarCache(session, root);
        var changes = 0;
        cache.AvatarChanged += (_, args) => { Assert.Equal(Account, args.AccountId); Interlocked.Increment(ref changes); };

        var cached = await cache.GetAsync(Url).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2 }, cached.Media.Content);
        Assert.False(response.Task.IsCompleted);
        response.SetResult(new RealmMediaResult([3, 4], "image/png"));
        await cache.WaitForRefreshAsync(Url);

        Assert.Equal(new byte[] { 3, 4 }, (await cache.GetAsync(Url)).Media.Content);
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, changes);
        Assert.Equal(1, session.MediaCalls);
        using var restarted = new AvatarCache(new AvatarSession { Account = Account, Read = _ => Task.FromException<RealmMediaResult>(new IOException()) }, root);
        Assert.Equal(new byte[] { 3, 4 }, (await restarted.GetAsync(Url)).Media.Content);
    }

    [Fact]
    public async Task GetAsync_WhenRefreshBytesAreIdentical_PreservesCachedObjectAndFileWithoutNotification()
    {
        var root = CreateRoot();
        var path = await SeedAsync(root, Account, [1, 2]);
        var originalWriteTime = File.GetLastWriteTimeUtc(path);
        var response = new TaskCompletionSource<RealmMediaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AvatarSession { Account = Account, Read = _ => response.Task };
        using var cache = new AvatarCache(session, root);
        var changes = 0;
        cache.AvatarChanged += (_, _) => changes++;
        var before = await cache.GetAsync(Url);
        response.SetResult(new RealmMediaResult([1, 2], "image/png"));
        await cache.WaitForRefreshAsync(Url);
        var after = await cache.GetAsync(Url);
        Assert.Same(before, after);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task GetAsync_WhenOffline_KeepsLocalImageAndRefreshesOnReconnect()
    {
        var root = CreateRoot();
        await SeedAsync(root, Account, [1]);
        var session = new AvatarSession { Account = Account, Read = _ => Task.FromException<RealmMediaResult>(new IOException()) };
        using var cache = new AvatarCache(session, root);
        Assert.Equal(new byte[] { 1 }, (await cache.GetAsync(Url)).Media.Content);
        await Assert.ThrowsAsync<IOException>(() => cache.WaitForRefreshAsync(Url));
        Assert.Equal(new byte[] { 1 }, (await cache.GetAsync(Url)).Media.Content);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.AvatarChanged += (_, _) => changed.TrySetResult();
        session.Read = _ => Task.FromResult(new RealmMediaResult([2], "image/png"));
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Connected) };
        session.Publish();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 2 }, (await cache.GetAsync(Url)).Media.Content);
        Assert.Equal(2, session.MediaCalls);
    }

    [Fact]
    public async Task GetAsync_WhenSeveralViewsRequestSameAvatar_CoalescesAndCancelsOnlyCaller()
    {
        var response = new TaskCompletionSource<RealmMediaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AvatarSession { Account = Account, Read = _ => { entered.TrySetResult(); return response.Task; } };
        using var cache = new AvatarCache(session, CreateRoot());
        using var cancellation = new CancellationTokenSource();
        var first = cache.GetAsync(Url, cancellation.Token);
        var others = Enumerable.Range(0, 8).Select(_ => cache.GetAsync(Url)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        response.SetResult(new RealmMediaResult([2], "image/png"));
        var avatars = await Task.WhenAll(others);
        Assert.All(avatars, avatar => Assert.Same(avatars[0], avatar));
        Assert.Equal(1, session.MediaCalls);
    }

    [Fact]
    public async Task GetAsync_WhenAccountChanges_DiscardsLateResponseAndReadsOtherAccountsFile()
    {
        var root = CreateRoot();
        var other = AccountId.Create(RealmEndpoint.Parse("https://avatar.example.test"), 8);
        await SeedAsync(root, other, [8]);
        var response = new TaskCompletionSource<RealmMediaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AvatarSession { Account = Account, Read = _ => { entered.TrySetResult(); return response.Task; } };
        using var cache = new AvatarCache(session, root);
        var first = cache.GetAsync(Url);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Account = other;
        session.Read = _ => Task.FromResult(new RealmMediaResult([8], "image/png"));
        session.Publish();
        response.SetResult(new RealmMediaResult([7], "image/png"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(new byte[] { 8 }, (await cache.GetAsync(Url)).Media.Content);
        Assert.False(Directory.Exists(NotificationAvatarFileStore.GetAccountCacheDirectory(root, Account)));
    }

    [Fact]
    public async Task ClearAccountAsync_WhenDownloadIsPending_PreventsRepopulationAndPreservesOtherAccount()
    {
        var root = CreateRoot();
        var other = AccountId.Create(RealmEndpoint.Parse("https://avatar.example.test"), 8);
        var ownPath = await SeedAsync(root, Account, [1]);
        var otherPath = await SeedAsync(root, other, [8]);
        var response = new TaskCompletionSource<RealmMediaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AvatarSession { Account = Account, Read = _ => { entered.TrySetResult(); return response.Task; } };
        using var cache = new AvatarCache(session, root);
        await cache.GetAsync(Url);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refresh = cache.WaitForRefreshAsync(Url);
        await cache.ClearAccountAsync(Account);
        response.SetResult(new RealmMediaResult([2], "image/png"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.False(File.Exists(ownPath));
        Assert.True(File.Exists(otherPath));
    }

    [Fact]
    public async Task GetAvatarUriAsync_WhenUsedByNotification_SharesUiCacheAndChangedFile()
    {
        var root = CreateRoot();
        await SeedAsync(root, Account, [1]);
        var response = new TaskCompletionSource<RealmMediaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AvatarSession { Account = Account, Read = _ => response.Task };
        using var cache = new AvatarCache(session, root);
        var notifications = new NotificationAvatarFileStore(cache);
        var uri = await notifications.GetAvatarUriAsync(Url);
        var image = await cache.GetAsync(Url);
        response.SetResult(new RealmMediaResult([2], "image/png"));
        await cache.WaitForRefreshAsync(Url);
        Assert.NotSame(image, await cache.GetAsync(Url));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(uri!.LocalPath));
        Assert.Equal(1, session.MediaCalls);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    public async Task GetAsync_WhenResponseIsNotSupported_DoesNotPersistIt(string contentType)
    {
        var root = CreateRoot();
        var session = new AvatarSession { Account = Account, Read = _ => Task.FromResult(new RealmMediaResult([1], contentType)) };
        using var cache = new AvatarCache(session, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Url));
        Assert.False(Directory.Exists(NotificationAvatarFileStore.GetAccountCacheDirectory(root, Account)));
    }

    private static string CreateRoot() => Path.Combine(Path.GetTempPath(), "RelayCove-avatar-tests", Guid.NewGuid().ToString("N"));

    private static async Task<string> SeedAsync(string root, AccountId account, byte[] bytes, string url = Url)
    {
        var directory = NotificationAvatarFileStore.GetAccountCacheDirectory(root, account);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, NotificationAvatarFileStore.CreateFileStem(account, url) + ".png");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    internal sealed class AvatarSession : IClientSession
    {
        public AccountId? Account { get; set; }
        public int SelectConversationCalls { get; private set; }
        public Task? SelectConversationGate { get; set; }
        public int MediaCalls;
        public Func<CancellationToken, Task<RealmMediaResult>> Read { get; set; } = _ => Task.FromResult(new RealmMediaResult([1], "image/png"));
        public void Publish() => StateChanged?.Invoke(this, new(StateValue));
        public ClientState StateValue { get; set; } = ClientState.Empty;
        public ConversationKey? Selected { get; set; }
        public IReadOnlyList<ConversationKey> Recent { get; set; } = [];
        public AccountId? AccountId => Account;
        public RealmEndpoint? ActiveRealm => null;
        public long? CurrentUserId => 1;
        public long MaxFileUploadBytes => 1_000_000;
        public ClientState State => StateValue;
        public ConversationKey? SelectedConversation => Selected;
        public ConversationHistoryState HistoryState => ConversationHistoryState.Empty;
        public IReadOnlyList<ConversationKey> RecentDirectMessages => Recent;
        public event EventHandler<ClientStateChangedEventArgs>? StateChanged;
        public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default)
        {
            SelectConversationCalls++;
            Selected = conversation;
            StateChanged?.Invoke(this, new(StateValue));
            return SelectConversationGate ?? Task.CompletedTask;
        }
        public Task LoadOlderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TopicSummary>>([]);
        public Task SendAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default) => Task.FromResult(new UploadedAttachment(upload.FileName, "https://example.test/file"));
        public Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default) { Interlocked.Increment(ref MediaCalls); return Read(cancellationToken); }
        public Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkDisplayedReadAsync(ConversationKey expectedConversation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
