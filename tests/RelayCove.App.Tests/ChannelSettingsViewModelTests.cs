using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class ChannelSettingsViewModelTests
{
    [Fact]
    public async Task OpenAsync_WhenSnapshotLoads_ProjectsFilterAndAdministratorAccess()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);

        await viewModel.OpenAsync(2);

        Assert.True(viewModel.IsOpen);
        Assert.Equal("engineering", viewModel.SelectedName);
        Assert.True(viewModel.CanAdminister);
        Assert.Single(viewModel.FilteredChannels);
        viewModel.ListMode = ChannelSettingsListMode.Available;
        Assert.Single(viewModel.FilteredChannels);
        Assert.Equal("design", viewModel.FilteredChannels.Single().Name);
    }

    [Fact]
    public async Task SaveEditAsync_WhenConfirmed_UpdatesOnlySelectedChannelAndReloads()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        viewModel.BeginEditNameCommand.Execute(null);
        viewModel.EditValue = "platform";

        await ((IAsyncRelayCommand)viewModel.SaveEditCommand).ExecuteAsync(null);

        Assert.Equal((2L, "platform", (string?)null, (long?)null, false), session.LastUpdate);
        Assert.False(viewModel.IsEditDialogOpen);
    }

    [Fact]
    public async Task ConfirmAsync_WhenArchiveRequested_UsesSelectedChannelOnly()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        viewModel.RequestArchiveCommand.Execute(null);

        await ((IAsyncRelayCommand)viewModel.ConfirmCommand).ExecuteAsync(null);

        Assert.Equal(2, session.ArchivedChannelId);
    }

    [Fact]
    public async Task FetchEmailAsync_WhenAllowed_CopiesOnlyAfterExplicitCommand()
    {
        var session = new SettingsSession();
        var interactions = new Interactions();
        using var viewModel = new ChannelSettingsViewModel(session, interactions, _ => Task.CompletedTask);
        await viewModel.OpenAsync(2);

        await ((IAsyncRelayCommand)viewModel.FetchEmailCommand).ExecuteAsync(null);
        Assert.Empty(interactions.Copied);
        await ((IAsyncRelayCommand)viewModel.CopyEmailCommand).ExecuteAsync(null);

        Assert.Equal("engineering@example.test", Assert.Single(interactions.Copied));
    }

    [Fact]
    public async Task FetchEmailAsync_WhenChannelChanges_DropsSupersededAddress()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SettingsSession
        {
            EmailHandler = async (_, cancellationToken) =>
            {
                requested.SetResult();
                return await response.Task.WaitAsync(cancellationToken);
            }
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        var fetch = ((IAsyncRelayCommand)viewModel.FetchEmailCommand).ExecuteAsync(null);
        await requested.Task;
        await ((IAsyncRelayCommand)viewModel.SelectChannelCommand).ExecuteAsync(
            viewModel.Channels.Single(channel => channel.ChannelId == 3));
        response.TrySetResult("engineering@example.test");
        await fetch;

        Assert.Equal(3, viewModel.SelectedChannel?.ChannelId);
        Assert.Null(viewModel.EmailAddress);
    }

    [Fact]
    public async Task CloseTopLayer_WhenChildDialogIsOpen_ClosesOnlyChildFirst()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        viewModel.BeginEditNameCommand.Execute(null);
        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsEditDialogOpen);

        viewModel.OpenCreateFolderCommand.Execute(null);
        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsCreateFolderOpen);

        viewModel.RequestUnsubscribeCommand.Execute(null);
        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsConfirmationOpen);

        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public async Task FolderAndDescriptionEdits_WhenCleared_RemainExplicitWrites()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        viewModel.ClearFolderCommand.Execute(null);
        Assert.True(viewModel.IsFolderDirty);
        await ((IAsyncRelayCommand)viewModel.SaveFolderCommand).ExecuteAsync(null);
        Assert.Equal((2L, (string?)null, (string?)null, (long?)null, true), session.LastUpdate);

        viewModel.BeginEditDescriptionCommand.Execute(null);
        viewModel.EditValue = string.Empty;
        await ((IAsyncRelayCommand)viewModel.SaveEditCommand).ExecuteAsync(null);
        Assert.Equal((2L, (string?)null, string.Empty, (long?)null, false), session.LastUpdate);
    }

    [Fact]
    public async Task DraftFolder_WhenPickerSelectsItem_ProjectsStableFolderId()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        var folder = Assert.Single(viewModel.Folders);
        viewModel.DraftFolder = folder;
        Assert.Equal(9, viewModel.DraftFolderId);
        Assert.False(viewModel.IsFolderDirty);

        viewModel.DraftFolder = null;
        Assert.Null(viewModel.DraftFolderId);
        Assert.True(viewModel.IsFolderDirty);
    }

    [Fact]
    public async Task ConfirmAsync_WhenUnsubscribeRequested_UsesSettingsSelection()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        viewModel.RequestUnsubscribeCommand.Execute(null);
        await ((IAsyncRelayCommand)viewModel.ConfirmCommand).ExecuteAsync(null);

        Assert.Equal(2, session.UnsubscribedChannelId);
    }

    [Fact]
    public async Task UpdateViewport_WhenNarrow_ShowsExactlyOnePane()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        viewModel.UpdateViewport(640);
        await viewModel.OpenAsync(2);

        Assert.True(viewModel.IsNarrowDetailVisible);
        Assert.Equal(new GridLength(0), viewModel.ListPaneWidth);
        Assert.Equal(GridLength.Star, viewModel.DetailPaneWidth);

        viewModel.BackToListCommand.Execute(null);
        Assert.True(viewModel.IsNarrowListVisible);
        Assert.Equal(GridLength.Star, viewModel.ListPaneWidth);
        Assert.Equal(new GridLength(0), viewModel.DetailPaneWidth);
    }

    private static ChannelSettingsViewModel Create(SettingsSession session) =>
        new(session, new Interactions(), _ => Task.CompletedTask);

    private sealed class Interactions : IPlatformInteractionService
    {
        public List<string> Copied { get; } = [];
        public Task CopyTextAsync(string text, CancellationToken cancellationToken = default) { Copied.Add(text); return Task.CompletedTask; }
        public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SettingsSession : IClientSession
    {
        private readonly ChannelSettingsSnapshot _snapshot = new(
            [new ChannelSummary(2, "engineering", "Build work", false, 4, false, true, "#336699", 12), new ChannelSummary(3, "design", null, false, 1)],
            [new ChannelFolder(9, "产品", null)], [], 10, true, false, new ChannelSettingsLimits(60, 1024, 60, 1024));
        public (long ChannelId, string? Name, string? Description, long? FolderId, bool ClearFolder) LastUpdate { get; private set; }
        public long? ArchivedChannelId { get; private set; }
        public long? UnsubscribedChannelId { get; private set; }
        public Func<long, CancellationToken, Task<string>>? EmailHandler { get; init; }
        public AccountId? AccountId => null;
        public RealmEndpoint? ActiveRealm => null;
        public long? CurrentUserId => 10;
        public long MaxFileUploadBytes => 0;
        public ClientState State => ClientState.Empty;
        public ConversationKey? SelectedConversation => null;
        public ConversationHistoryState HistoryState => ConversationHistoryState.Empty;
        public IReadOnlyList<ConversationKey> RecentDirectMessages => [];
        public event EventHandler<ClientStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadOlderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TopicSummary>>([]);
        public Task SendAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default) => Task.FromResult(new UploadedAttachment("x", "https://example.test/x"));
        public Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new RealmMediaResult([], "image/png"));
        public Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default) { UnsubscribedChannelId = channelId; return Task.CompletedTask; }
        public Task<ChannelSettingsSnapshot> LoadChannelSettingsSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
        public Task<ChannelDetails> LoadChannelDetailsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromResult(new ChannelDetails(channelId, channelId == 2 ? "engineering" : "design", "Build work", false, false, false, 4, 12, 9, 10, DateTimeOffset.UnixEpoch, null, null, new AnonymousChannelGroupSetting([10], []), new AnonymousChannelGroupSetting([10], []), new AnonymousChannelGroupSetting([10], [])));
        public Task UpdateChannelAsync(long channelId, string? name, string? description, long? folderId, bool clearFolder = false, CancellationToken cancellationToken = default) { LastUpdate = (channelId, name, description, folderId, clearFolder); return Task.CompletedTask; }
        public Task<ChannelFolder> CreateChannelFolderAsync(string name, string? description, CancellationToken cancellationToken = default) => Task.FromResult(new ChannelFolder(10, name, description));
        public Task<string> GetChannelEmailAddressAsync(long channelId, CancellationToken cancellationToken = default) =>
            EmailHandler?.Invoke(channelId, cancellationToken) ?? Task.FromResult("engineering@example.test");
        public Task ArchiveChannelAsync(long channelId, CancellationToken cancellationToken = default) { ArchivedChannelId = channelId; return Task.CompletedTask; }
        public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkDisplayedReadAsync(ConversationKey expectedConversation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
