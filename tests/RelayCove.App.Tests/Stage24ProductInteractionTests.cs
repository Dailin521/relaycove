using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class Stage24ProductInteractionTests
{
    [Fact]
    public void Projection_WhenSummaryExistsAndMessageWindowIsEmpty_KeepsNavigationPreviewAndFiltersIt()
    {
        var conversation = new DirectMessage([2]);
        var message = new ChatMessage(8, conversation, 2, "summary preview", DateTimeOffset.UnixEpoch);
        var session = new TestSession
        {
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [2] = new UserProfile(2, "Dal", isActive: true) },
                conversationSummaries: new Dictionary<string, ConversationSummary> { [conversation.CanonicalKey] = new(conversation, message) })
        };
        using var viewModel = Create(session);

        Assert.Single(viewModel.DirectMessages);
        Assert.Equal("summary preview", viewModel.DirectMessages[0].Detail);
        viewModel.ConversationFilterQuery = "summary";
        Assert.Single(viewModel.FilteredDirectMessages);
        viewModel.ClearConversationFilter();
        Assert.Single(viewModel.FilteredDirectMessages);
    }

    [Fact]
    public void PreferencesAndViewport_WhenUsingContinuousValues_PreserveValuesAndUse820IntermediateRail()
    {
        var preferences = new TestPreferences { Current = new UiPreferences(FontSize: 13d, ConversationPaneWidth: 296d) };
        using var viewModel = Create(new TestSession(), preferences);

        Assert.Equal(13d, viewModel.FontScaleSliderValue);
        Assert.Equal(296d, viewModel.ConversationWidthSliderValue);
        viewModel.UpdateViewport(820d);

        Assert.True(viewModel.IsIntermediateLayout);
        Assert.Equal(48d, viewModel.NavigationRailWidth.Value);
    }

    [Fact]
    public void ActivateDirectMessage_WhenItemIsAlreadySelected_RefreshesConversationAgain()
    {
        var conversation = new DirectMessage([2]);
        var session = new TestSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [2] = new UserProfile(2, "Dal", isActive: true) })
        };
        using var viewModel = Create(session);
        var item = Assert.Single(viewModel.DirectMessages);

        viewModel.ActivateDirectMessage(item);

        Assert.Equal(1, session.SelectConversationCalls);
        Assert.Equal(conversation, session.Selected);
    }

    [Fact]
    public void RealmMediaService_WhenAccountChanges_UsesDifferentAvatarCacheScopes()
    {
        var first = AccountId.Create(RealmEndpoint.Parse("https://first.example.test"), 1);
        var second = AccountId.Create(RealmEndpoint.Parse("https://second.example.test"), 2);
        const string avatar = "/user_avatars/shared/avatar.png";

        Assert.NotEqual(
            RealmMediaService.CreateCacheKey(first, RealmMediaKind.Avatar, avatar),
            RealmMediaService.CreateCacheKey(second, RealmMediaKind.Avatar, avatar));
    }

    private static ShellViewModel Create(TestSession session, TestPreferences? preferences = null) => new(
        session, new TestLastRealmStore(), new TestDispatcher(), new TestAppearance(), preferences ?? new TestPreferences(),
        new TestInteractions(), new TestFiles(), new TestMedia(), new TestSave());

    private sealed class TestSession : IClientSession
    {
        public AccountId? Account { get; set; }
        public int SelectConversationCalls { get; private set; }
        public int MediaCalls { get; private set; }
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
        public Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default) { SelectConversationCalls++; Selected = conversation; StateChanged?.Invoke(this, new(StateValue)); return Task.CompletedTask; }
        public Task LoadOlderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TopicSummary>>([]);
        public Task SendAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default) => Task.FromResult(new UploadedAttachment(upload.FileName, "https://example.test/file"));
        public Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default) { MediaCalls++; return Task.FromResult(new RealmMediaResult([1], "image/png")); }
        public Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestPreferences : IUiPreferencesService { public UiPreferences Current { get; set; } = new(); public void Save(UiPreferences preferences) => Current = preferences; public UiPreferences Reset() => Current = new(); }
    private sealed class TestLastRealmStore : ILastRealmStore { public string Get() => PreferencesLastRealmStore.DefaultRealm; public void Set(string realm) { } }
    private sealed class TestDispatcher : IUiDispatcher { public void Dispatch(Action action) => action(); }
    private sealed class TestAppearance : IAppearanceService { public AppAppearanceMode Current => AppAppearanceMode.System; public void Apply(AppAppearanceMode mode) { } }
    private sealed class TestInteractions : IPlatformInteractionService { public Task CopyTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask; public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class TestFiles : IFileSelectionService { public Task<IReadOnlyList<SelectedAttachmentFile>> PickMultipleAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SelectedAttachmentFile>>([]); }
    private sealed class TestMedia : IRealmMediaService { public Task<Microsoft.Maui.Controls.ImageSource> GetImageAsync(string sourceUrl, RealmMediaKind kind, CancellationToken cancellationToken = default) => Task.FromResult(Microsoft.Maui.Controls.ImageSource.FromStream(() => new MemoryStream())); public Task<RealmMediaResult> GetFileAsync(string sourceUrl, CancellationToken cancellationToken = default) => Task.FromResult(new RealmMediaResult([], "image/png")); }
    private sealed class TestSave : IFileSaveService { public Task<bool> SaveAsync(string fileName, byte[] content, CancellationToken cancellationToken = default) => Task.FromResult(true); }
}
