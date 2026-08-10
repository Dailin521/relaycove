using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void Constructor_WhenNoLastRealm_UsesConfiguredDefault()
    {
        using var viewModel = CreateViewModel(new FakeSession(), new FakeLastRealmStore());

        Assert.Equal(PreferencesLastRealmStore.DefaultRealm, viewModel.Realm);
    }

    [Fact]
    public void Constructor_WhenLastRealmExists_UsesIt()
    {
        using var viewModel = CreateViewModel(new FakeSession(), new FakeLastRealmStore("https://chat.example.test"));

        Assert.Equal("https://chat.example.test", viewModel.Realm);
    }

    [Fact]
    public async Task LoginCommand_WhenAuthenticationFails_ClassifiesErrorAndClearsPassword()
    {
        var session = new FakeSession
        {
            LoginAction = (_, _, _, _) => throw new GatewayException(
                GatewayErrorKind.AuthenticationFailed,
                GatewayErrorCode.AuthenticationFailed)
        };
        using var viewModel = CreateViewModel(session);
        viewModel.Email = "person@example.test";
        viewModel.Password = "do-not-log";

        await ((IAsyncRelayCommand)viewModel.LoginCommand).ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal("邮箱、密码或 API 凭据无效。", viewModel.LoginError);
    }

    [Fact]
    public void SessionStateChanged_WhenSubscribedAndRecentDirectMessage_ProjectsNavigationAndRawMessage()
    {
        var direct = new DirectMessage([8]);
        var channel = new ChannelTopic(4, "release");
        var state = new ClientState(
            messages: new Dictionary<long, ChatMessage>
            {
                [11] = new ChatMessage(11, channel, 7, "**raw markdown**", DateTimeOffset.UnixEpoch, senderDisplayName: "Ada")
            },
            subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
            users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var session = new FakeSession { StateValue = state, Recent = [direct], Selected = channel };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.True(viewModel.MainVisible);
        Assert.Single(viewModel.Channels);
        Assert.Equal("engineering", viewModel.Channels[0].Name);
        Assert.Single(viewModel.DirectMessages);
        Assert.Equal("Bea", viewModel.DirectMessages[0].Title);
        Assert.Single(viewModel.Messages);
        Assert.Equal("**raw markdown**", viewModel.Messages[0].Content);
    }

    [Fact]
    public async Task LoginCommand_WhenAlreadyExecuting_RejectsConcurrentExecution()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            LoginAction = async (_, _, _, cancellationToken) => await blocker.Task.WaitAsync(cancellationToken)
        };
        using var viewModel = CreateViewModel(session);

        var first = ((IAsyncRelayCommand)viewModel.LoginCommand).ExecuteAsync(null);
        await Task.Yield();
        var second = ((IAsyncRelayCommand)viewModel.LoginCommand).ExecuteAsync(null);
        blocker.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, session.LoginCalls);
    }

    [Fact]
    public void SessionStateChanged_WhenOutboxResultIsUncertain_AllowsExplicitComposerRecovery()
    {
        var conversation = new DirectMessage([8]);
        var outbox = new OutboxEntry(
            "9",
            conversation,
            "recover this raw text",
            DateTimeOffset.UnixEpoch,
            OutboxState.WaitExpired);
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(
                outbox: new Dictionary<string, OutboxEntry> { [outbox.LocalId] = outbox },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        session.Publish();
        var message = Assert.Single(viewModel.Messages);
        viewModel.RecoverOutboxCommand.Execute(message);

        Assert.True(message.CanRecover);
        Assert.Equal("recover this raw text", viewModel.ComposerText);
        Assert.Contains("可能产生重复消息", message.DeliveryState, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStateChanged_WhenAuthenticatedCacheFaults_KeepsMainVisibleForLogoutAndCachedData()
    {
        var session = new FakeSession
        {
            Account = RelayCove.Core.AccountId.Create(
                RealmEndpoint.Parse("https://zulip.example"), 10),
            StateValue = new ClientState(connection: new ConnectionState(
                RelayCove.Core.ConnectionStatus.Faulted,
                "local_store_error"))
        };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.True(viewModel.MainVisible);
        Assert.Equal("连接故障", viewModel.ConnectionStatus);
    }

    [Fact]
    public async Task LogoutCommand_WhenSecurityCleanupFails_PreservesActionableError()
    {
        var session = new FakeSession
        {
            LogoutAction = _ => throw new AggregateException(new InvalidOperationException("cleanup"))
        };
        using var viewModel = CreateViewModel(session);

        await ((IAsyncRelayCommand)viewModel.LogoutCommand).ExecuteAsync(null);

        Assert.Equal("注销未完全完成，请重试以安全删除凭据并锁定本地缓存。", viewModel.LoginError);
    }

    private static ShellViewModel CreateViewModel(FakeSession session, FakeLastRealmStore? lastRealmStore = null) =>
        new(session, lastRealmStore ?? new FakeLastRealmStore(), new InlineDispatcher());

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Dispatch(Action action) => action();
    }

    private sealed class FakeLastRealmStore(string? value = null) : ILastRealmStore
    {
        private string? _value = value;

        public string Get() => _value ?? PreferencesLastRealmStore.DefaultRealm;
        public void Set(string realm) => _value = realm;
    }

    private sealed class FakeSession : IClientSession
    {
        public ClientState StateValue { get; set; } = ClientState.Empty;
        public ConversationKey? Selected { get; set; }
        public IReadOnlyList<ConversationKey> Recent { get; set; } = [];
        public AccountId? Account { get; set; }
        public Func<string, string, string, CancellationToken, Task>? LoginAction { get; set; }
        public Func<CancellationToken, Task>? LogoutAction { get; set; }
        public int LoginCalls { get; private set; }

        public AccountId? AccountId => Account;
        public ClientState State => StateValue;
        public ConversationKey? SelectedConversation => Selected;
        public IReadOnlyList<ConversationKey> RecentDirectMessages => Recent;
        public event EventHandler<ClientStateChangedEventArgs>? StateChanged;

        public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default)
        {
            LoginCalls++;
            return LoginAction?.Invoke(realm, email, password, cancellationToken) ?? Task.CompletedTask;
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default) =>
            LogoutAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        public Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default)
        {
            Selected = conversation;
            return Task.CompletedTask;
        }

        public Task LoadOlderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TopicSummary>>([]);

        public Task SendAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Publish() => StateChanged?.Invoke(this, new ClientStateChangedEventArgs(StateValue));
    }
}
