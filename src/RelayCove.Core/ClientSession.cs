using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelayCove.Core;

public sealed class ClientSession : IClientSession, IMessageMutationObserver, IRealtimeMessageObserver, IAsyncDisposable
{
    private static long s_nextLocalId;
    private static readonly TimeSpan ServerRestartRecoveryWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PresenceRefreshInterval = TimeSpan.FromSeconds(60);
    private const int HistoryPageSize = 50;
    private const int MessageWindowLimit = 250;
    private const int HistoryMemoryCacheLimit = 12;

    private readonly IZulipGateway _gateway;
    private readonly IAccountStore _store;
    private readonly ICredentialVault _vault;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan, CancellationToken, Task> _sendDeadlineDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _presenceDelay;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan> _serverRestartDelay;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _ownPresenceLane = new(1, 1);
    private readonly SemaphoreSlim _ownUserStatusLane = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly ConcurrentDictionary<string, Task> _outboxTimers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _messageMutationLanes = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _channelUnsubscribeLanes = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _channelPreferenceLanes = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _channelSubscribeLanes = new();
    private readonly Dictionary<string, ChatMessage[]> _historyMemoryCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _historyMemoryLru = [];
    private IReadOnlyDictionary<long, ChannelSummary> _availableChannels = new Dictionary<long, ChannelSummary>();
    private CancellationTokenSource? _channelCatalogCancellation;
    private long _channelCatalogGeneration;
    private CancellationTokenSource? _channelSettingsCancellation;
    private long _channelSettingsGeneration;
    private ChannelSettingsSnapshot? _channelSettingsSnapshot;
    private ChannelSettingsLimits _channelSettingsLimits = new(null, null, null, null);
    private IReadOnlyDictionary<string, TopicVisibilityPolicy> _topicVisibilityPolicies = new Dictionary<string, TopicVisibilityPolicy>(StringComparer.Ordinal);
    private bool _isOrganizationAdministrator;
    private bool _canCreatePrivateGroup;
    private bool _isPresenceAvailable;
    private bool? _isOwnPresenceEnabled;
    private UserPresenceStatus? _ownPresenceStatus;
    private bool _isUserStatusAvailable;
    private bool _isOwnUserStatusConfirmed;
    private UserStatusContent? _pendingOwnUserStatusConfirmation;
    private long _pendingOwnUserStatusAfterEventId;
    private long? _lastOwnUserStatusEventId;
    private UserStatusContent? _lastOwnUserStatusEventValue;

    private ClientState _state = ClientState.Empty;
    private AccountId? _accountId;
    private ConversationKey? _selectedConversation;
    private ConversationHistoryState _historyState = ConversationHistoryState.Empty;
    private long _historyGeneration;
    private Task? _latestHistoryTask;
    private Task? _loadOlderTask;
    private CancellationTokenSource? _historyCancellation;
    private CancellationTokenSource? _searchQueryCancellation;
    private long _searchQueryGeneration;
    private CancellationTokenSource? _savedQueryCancellation;
    private long _savedQueryGeneration;
    private long _queryEpoch;
    private bool _retainOldestWindow;
    private IReadOnlyList<ConversationKey> _recentDirectMessages = [];
    private CredentialEnvelope? _credentials;
    private string? _queueId;
    private TimeSpan _longPollTimeout = TimeSpan.FromSeconds(30);
    private int _maxMessageLength = int.MaxValue;
    private int _maxTopicLength = int.MaxValue;
    private long _maxFileUploadBytes = 10L * 1024 * 1024;
    private CancellationTokenSource? _runCancellation;
    private Task? _eventLoop;
    private Task? _presenceLoop;
    private int _disposed;

    public ClientSession(
        IZulipGateway gateway,
        IAccountStore store,
        ICredentialVault vault,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan>? serverRestartDelay = null,
        Func<TimeSpan, CancellationToken, Task>? sendDeadlineDelay = null,
        Func<TimeSpan, CancellationToken, Task>? presenceDelay = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _delay = delay ?? Task.Delay;
        _sendDeadlineDelay = sendDeadlineDelay ?? Task.Delay;
        _presenceDelay = presenceDelay ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _serverRestartDelay = serverRestartDelay ?? (() => TimeSpan.FromMilliseconds(
            Random.Shared.NextDouble() * ServerRestartRecoveryWindow.TotalMilliseconds));
    }

    public AccountId? AccountId
    {
        get { lock (_stateGate) return _accountId; }
    }

    public RealmEndpoint? ActiveRealm
    {
        get { lock (_stateGate) return _credentials?.Realm; }
    }

    public long? CurrentUserId
    {
        get { lock (_stateGate) return _credentials?.UserId; }
    }

    public bool IsOrganizationAdministrator
    {
        get { lock (_stateGate) return _isOrganizationAdministrator; }
    }

    public bool CanCreatePrivateGroup
    {
        get { lock (_stateGate) return _canCreatePrivateGroup; }
    }

    public bool CanSetOwnPresence
    {
        get { lock (_stateGate) return _isPresenceAvailable && _isOwnPresenceEnabled is not null; }
    }

    public UserPresenceStatus? OwnPresenceStatus
    {
        get { lock (_stateGate) return _ownPresenceStatus; }
    }

    public bool CanSetOwnUserStatus
    {
        get { lock (_stateGate) return _isUserStatusAvailable; }
    }

    public UserStatusContent? OwnUserStatus
    {
        get
        {
            lock (_stateGate)
            {
                return _credentials is { UserId: var userId }
                    ? _state.UserStatuses.Users.GetValueOrDefault(userId)
                    : null;
            }
        }
    }

    public bool IsOwnUserStatusConfirmed
    {
        get { lock (_stateGate) return _isOwnUserStatusConfirmed; }
    }

    public long MaxFileUploadBytes
    {
        get { lock (_stateGate) return _maxFileUploadBytes; }
    }

    public ClientState State
    {
        get { lock (_stateGate) return _state; }
    }

    public ConversationKey? SelectedConversation
    {
        get { lock (_stateGate) return _selectedConversation; }
    }

    public ConversationHistoryState HistoryState
    {
        get { lock (_stateGate) return _historyState; }
    }

    public IReadOnlyList<ConversationKey> RecentDirectMessages
    {
        get { lock (_stateGate) return _recentDirectMessages.ToArray(); }
    }

    public event EventHandler<ClientStateChangedEventArgs>? StateChanged;
    public event EventHandler<MessageMutationObservedEventArgs>? MessageMutationObserved;
    public event EventHandler<RealtimeMessageReceivedEventArgs>? RealtimeMessageReceived;

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_credentials is not null) return true;
            await StopRunAsync(setOffline: false).ConfigureAwait(false);
            CredentialEnvelope? credentials;
            try
            {
                credentials = await _vault.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await LockAllCachesBestEffortAsync().ConfigureAwait(false);
                ResetInMemory(new ConnectionState(ConnectionStatus.Locked, "credential_unavailable"), clearAccount: true);
                return false;
            }
            if (credentials is null)
            {
                await LockAllCachesBestEffortAsync().ConfigureAwait(false);
                ResetInMemory(ConnectionState.SignedOut, clearAccount: true);
                return false;
            }

            var account = ToStoredAccount(credentials);
            await _store.InitializeAsync(account, cancellationToken).ConfigureAwait(false);
            await _store.MigrateAsync(account.AccountId, cancellationToken).ConfigureAwait(false);
            if (!await _store.IsCacheUnlockedAsync(account.AccountId, cancellationToken).ConfigureAwait(false))
            {
                await RejectLockedRestoreAsync(account).ConfigureAwait(false);
                return false;
            }
            var cached = await _store.LoadAsync(account.AccountId, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                _credentials = credentials;
                _accountId = account.AccountId;
                _state = FilterSupportedConversations((cached?.State ?? ClientState.Empty) with
                {
                    Connection = new ConnectionState(ConnectionStatus.Offline, "cache_first")
                });
                _recentDirectMessages = MergeRecentDirectMessages(
                    cached?.RecentDirectMessages ?? [],
                    DeriveRecentDirectMessages(_state));
                SeedHistoryMemoryCacheLocked(_state.Messages.Values);
            }
            RaiseStateChanged();

            try
            {
                var register = await _gateway.RegisterAsync(new RegisterRequest(credentials), cancellationToken).ConfigureAwait(false);
                await ApplyRegisterAsync(register, cancellationToken).ConfigureAwait(false);
                StartRun();
                return true;
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                return false;
            }
            catch (GatewayException exception) when (IsNetwork(exception) || IsRateLimited(exception))
            {
                StartRun();
                return true;
            }
            catch (GatewayException)
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Faulted, "register_failed") });
                return true;
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_credentials is not null) throw new InvalidOperationException("A session is already active.");
            await StopRunAsync(setOffline: false).ConfigureAwait(false);
            Mutate(state => ClientState.Empty with { Connection = new ConnectionState(ConnectionStatus.Connecting) });
            CredentialEnvelope credentials;
            StoredAccount account;
            try
            {
                var endpoint = RealmEndpoint.Parse(realm);
                var probe = await _gateway.ProbeRealmAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!probe.IsCompatible)
                {
                    throw new GatewayException(GatewayErrorKind.IncompatibleRealm, GatewayErrorCode.IncompatibleRealm);
                }

                var authentication = await _gateway.AuthenticateAsync(
                    new AuthenticationRequest(endpoint, email, password), cancellationToken).ConfigureAwait(false);
                credentials = authentication.Credentials;
                account = ToStoredAccount(credentials);
                await _store.InitializeAsync(account, cancellationToken).ConfigureAwait(false);
                await _store.MigrateAsync(account.AccountId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ResetInMemory(ConnectionState.SignedOut, clearAccount: true);
                throw;
            }
            lock (_stateGate)
            {
                _credentials = credentials;
                _accountId = account.AccountId;
            }

            try
            {
                await _vault.SetAsync(credentials, cancellationToken).ConfigureAwait(false);
                await _store.SetCacheUnlockedAsync(account.AccountId, true, cancellationToken).ConfigureAwait(false);
                var register = await _gateway.RegisterAsync(new RegisterRequest(credentials), cancellationToken).ConfigureAwait(false);
                await ApplyRegisterAsync(register, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception loginFailure)
            {
                try
                {
                    await CleanupFailedLoginAsync(account.AccountId).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Login failed and security cleanup was incomplete.", loginFailure, cleanupFailure);
                }
                throw;
            }
            StartRun();
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopRunAsync(setOffline: false).ConfigureAwait(false);
            CredentialEnvelope? credentials;
            string? queue;
            AccountId? accountId;
            lock (_stateGate)
            {
                credentials = _credentials;
                queue = _queueId;
                accountId = _accountId;
            }
            if (credentials is not null && queue is not null)
            {
                try
                {
                    await _gateway.DeleteQueueAsync(new DeleteQueueRequest(credentials, queue), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is GatewayException or OperationCanceledException)
                {
                    // Queue deletion is explicitly best effort.
                }
            }

            var failures = await RemoveCredentialAndLockAsync(accountId).ConfigureAwait(false);
            if (failures.Count == 0)
            {
                ResetInMemory(ConnectionState.SignedOut, clearAccount: true);
                return;
            }

            lock (_stateGate)
            {
                _credentials = null;
                _queueId = null;
                _selectedConversation = null;
                InvalidateHistoryLocked(clearConversation: true);
                _recentDirectMessages = [];
                _accountId = accountId;
                _state = ClientState.Empty with
                {
                    Connection = new ConnectionState(ConnectionStatus.Faulted, "logout_cleanup_failed")
                };
            }
            RaiseStateChanged();
            throw new AggregateException("Logout cleanup was incomplete.", failures);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ThrowIfDisposed();
        Task loadTask;
        var publish = false;
        CancellationTokenSource? priorHistoryCancellation = null;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                var accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                EnsureSupportedConversationLocked(conversation);
                CacheSelectedHistoryLocked();
                priorHistoryCancellation = _historyCancellation;
                var runToken = _runCancellation?.Token ?? _disposeCancellation.Token;
                _historyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _disposeCancellation.Token,
                    runToken,
                    cancellationToken);
                _selectedConversation = conversation;
                _state = _state with
                {
                    Messages = TryGetHistoryMemoryWindowLocked(conversation, out var memoryWindow)
                        ? memoryWindow.ToDictionary(message => message.Id)
                        : new Dictionary<long, ChatMessage>()
                };
                var generation = ++_historyGeneration;
                _historyState = new ConversationHistoryState(conversation, generation, true, false, false, null, null);
                _retainOldestWindow = false;
                _loadOlderTask = null;
                var credentials = _state.Connection.Status == ConnectionStatus.Connected ? _credentials : null;
                loadTask = LoadLatestAsync(accountId, credentials, conversation, generation, _historyCancellation.Token);
                _latestHistoryTask = loadTask;
                publish = true;
            }
        }
        finally
        {
            _commands.Release();
        }
        priorHistoryCancellation?.Cancel();
        priorHistoryCancellation?.Dispose();
        if (publish) RaiseStateChanged();
        await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task LoadOlderAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        Task task;
        lock (_stateGate)
        {
            var conversation = _selectedConversation ?? throw new InvalidOperationException("No conversation is selected.");
            var accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            var generation = _historyState.Generation;
            if (_historyState.FoundOldest) return Task.CompletedTask;
            if (_loadOlderTask is { IsCompleted: false } &&
                _historyState.Conversation == conversation)
            {
                return _loadOlderTask;
            }
            var minimum = MinimumMessageIdLocked(conversation);
            if (minimum is null) return Task.CompletedTask;
            var credentials = _state.Connection.Status == ConnectionStatus.Connected ? _credentials : null;
            _historyState = _historyState with { IsLoading = true, Error = null };
            _retainOldestWindow = true;
            var historyToken = _historyCancellation?.Token ?? _disposeCancellation.Token;
            task = LoadOlderCoreAsync(accountId, credentials, conversation, generation, minimum.Value, historyToken);
            _loadOlderTask = task;
        }
        RaiseStateChanged();
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    public Task<MessageQueryPage> SearchMessagesAsync(
        string query,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default,
        MessageSearchFilter filter = MessageSearchFilter.Messages)
    {
        if (string.IsNullOrWhiteSpace(query) && filter == MessageSearchFilter.Messages)
        {
            throw new ArgumentException("A search query or content filter is required.", nameof(query));
        }
        if (!Enum.IsDefined(filter)) throw new ArgumentOutOfRangeException(nameof(filter));
        return LoadMessageQueryAsync(
            MessageQueryKind.Search,
            (credentials, token) => _gateway.SearchMessagesAsync(
                new MessageSearchRequest(credentials, query.Trim(), beforeMessageId, limit, filter), token),
            cancellationToken);
    }

    public Task<MessageQueryPage> LoadSavedMessagesAsync(
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default) =>
        LoadMessageQueryAsync(
            MessageQueryKind.Saved,
            (credentials, token) => _gateway.LoadSavedMessagesAsync(
                new SavedMessagesRequest(credentials, beforeMessageId, limit), token),
            cancellationToken);

    public async Task OpenMessageAsync(
        ConversationKey conversation,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        ThrowIfDisposed();
        Task loadTask;
        CancellationTokenSource? priorHistoryCancellation;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                var accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                EnsureSupportedConversationLocked(conversation);
                var credentials = _state.Connection.Status == ConnectionStatus.Connected
                    ? _credentials ?? throw new InvalidOperationException("No credentials are available.")
                    : throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut);
                priorHistoryCancellation = _historyCancellation;
                var runToken = _runCancellation?.Token ?? _disposeCancellation.Token;
                _historyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _disposeCancellation.Token,
                    runToken,
                    cancellationToken);
                _selectedConversation = conversation;
                var generation = ++_historyGeneration;
                _state = _state with { Messages = new Dictionary<long, ChatMessage>() };
                _historyState = new ConversationHistoryState(conversation, generation, true, false, false, null, null);
                _retainOldestWindow = false;
                _loadOlderTask = null;
                loadTask = LoadMessageAroundAsync(
                    accountId,
                    credentials,
                    conversation,
                    messageId,
                    generation,
                    _historyCancellation.Token);
                _latestHistoryTask = loadTask;
            }
        }
        finally
        {
            _commands.Release();
        }
        priorHistoryCancellation?.Cancel();
        priorHistoryCancellation?.Dispose();
        RaiseStateChanged();
        await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        if (!PrivateGroupPolicy.IsEligible(State.Subscriptions.GetValueOrDefault(channelId)))
            throw new InvalidOperationException("Only RelayCove private groups have a supported channel conversation.");
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = State.Topics.Values
                .Where(topic => topic.ChannelId == channelId)
                .OrderByDescending(topic => topic.MaxMessageId)
                .ThenBy(topic => topic.Topic, StringComparer.Ordinal)
                .ToArray();
            CredentialEnvelope? credentials;
            lock (_stateGate)
            {
                credentials = _state.Connection.Status == ConnectionStatus.Connected
                    ? _credentials
                    : null;
            }
            if (credentials is null) return DecorateTopics(cached);
            try
            {
                var result = await _gateway.GetTopicsAsync(new TopicsRequest(credentials, channelId), cancellationToken).ConfigureAwait(false);
                var supportedTopics = result.Topics.Where(static topic => topic.Topic.Length == 0).ToArray();
                var events = supportedTopics.Select(topic => (DomainEvent)new TopicUpsertEvent(topic, Source: DomainEventSource.History)).ToArray();
                await StoreThenApplyAsync(events, cancellationToken).ConfigureAwait(false);
                return DecorateTopics(supportedTopics);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception) when (IsNetwork(exception))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline) });
                return DecorateTopics(cached);
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    private async Task<MessageQueryPage> LoadMessageQueryAsync(
        MessageQueryKind kind,
        Func<CredentialEnvelope, CancellationToken, Task<MessageQueryPage>> load,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested) return await Task.FromCanceled<MessageQueryPage>(cancellationToken).ConfigureAwait(false);

        CredentialEnvelope credentials;
        CancellationTokenSource requestCancellation;
        CancellationTokenSource? previousCancellation;
        long generation;
        AccountId accountId;
        long epoch;
        CancellationTokenSource? runCancellation;
        lock (_stateGate)
        {
            credentials = _state.Connection.Status == ConnectionStatus.Connected
                ? _credentials ?? throw new InvalidOperationException("No credentials are available.")
                : throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut);
            accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            runCancellation = _runCancellation;
            var runToken = runCancellation?.Token ?? _disposeCancellation.Token;
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
            previousCancellation = kind == MessageQueryKind.Search ? _searchQueryCancellation : _savedQueryCancellation;
            if (kind == MessageQueryKind.Search)
            {
                _searchQueryCancellation = requestCancellation;
                generation = ++_searchQueryGeneration;
            }
            else
            {
                _savedQueryCancellation = requestCancellation;
                generation = ++_savedQueryGeneration;
            }
            epoch = _queryEpoch;
        }
        previousCancellation?.Cancel();

        try
        {
            var result = await load(credentials, requestCancellation.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (!IsMessageQueryCurrentLocked(kind, generation, accountId, epoch, runCancellation))
                    throw new OperationCanceledException(requestCancellation.Token);
            }
            lock (_stateGate)
            {
                return result with
                {
                    Messages = result.Messages
                        .Where(message => IsSupportedConversation(_state, message.Conversation))
                        .ToArray()
                };
            }
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsMessageQueryCurrent(kind, generation, accountId, epoch, runCancellation))
                await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }
        catch (GatewayException exception) when (IsNetwork(exception))
        {
            if (IsMessageQueryCurrent(kind, generation, accountId, epoch, runCancellation))
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline, "message_query_offline") });
            throw;
        }
        catch (GatewayException exception) when (IsRateLimited(exception))
        {
            if (IsMessageQueryCurrent(kind, generation, accountId, epoch, runCancellation))
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.RateLimited, "message_query_rate_limited") });
            throw;
        }
        finally
        {
            lock (_stateGate)
            {
                if (kind == MessageQueryKind.Search && ReferenceEquals(_searchQueryCancellation, requestCancellation))
                {
                    _searchQueryCancellation = null;
                }
                else if (kind == MessageQueryKind.Saved && ReferenceEquals(_savedQueryCancellation, requestCancellation))
                {
                    _savedQueryCancellation = null;
                }
            }
            requestCancellation.Dispose();
        }
    }

    public Task SendAsync(string content, CancellationToken cancellationToken = default)
    {
        ConversationKey conversation;
        lock (_stateGate)
        {
            conversation = _selectedConversation ?? throw new InvalidOperationException("No conversation is selected.");
        }
        return SendAsync(conversation, content, cancellationToken);
    }

    public async Task SendAsync(
        ConversationKey expectedConversation,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedConversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            CredentialEnvelope credentials;
            ConversationKey conversation;
            string queueId;
            CancellationToken runToken;
            lock (_stateGate)
            {
                if (_state.Connection.Status != ConnectionStatus.Connected) throw new InvalidOperationException("Sending requires a connected session.");
                credentials = _credentials ?? throw new InvalidOperationException("No credentials are available.");
                conversation = _selectedConversation ?? throw new InvalidOperationException("No conversation is selected.");
                if (!string.Equals(conversation.CanonicalKey, expectedConversation.CanonicalKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The selected conversation changed before send.");
                }
                queueId = _queueId ?? throw new InvalidOperationException("No event queue is registered.");
                ValidateSend(conversation, content);
                runToken = _runCancellation?.Token ?? throw new InvalidOperationException("The session is stopped.");
            }

            var localId = Interlocked.Increment(ref s_nextLocalId).ToString(CultureInfo.InvariantCulture);
            var entry = new OutboxEntry(localId, conversation, content, _utcNow(), OutboxState.Hidden);
            Mutate(state => DomainReducer.Apply(state, new OutboxQueuedEvent(entry)));
            StartOutboxTimer(localId, runToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
            SendResult result;
            var deadlineExpired = false;
            try
            {
                var send = _gateway.SendAsync(
                    new SendRequest(credentials, queueId, localId, conversation, content), linked.Token);
                if (!send.IsCompleted)
                {
                    using var deadlineCancellation = new CancellationTokenSource();
                    var deadline = _sendDeadlineDelay(OutboxTimingPolicy.ExpiryDuration, deadlineCancellation.Token);
                    var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                    var completed = await Task.WhenAny(send, deadline, cancelled).ConfigureAwait(false);
                    if (completed == cancelled)
                    {
                        deadlineCancellation.Cancel();
                        ObserveAfterCancellation(send);
                        cancellationToken.ThrowIfCancellationRequested();
                        runToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException(linked.Token);
                    }
                    if (completed == deadline && !send.IsCompleted)
                    {
                        await deadline.ConfigureAwait(false);
                        deadlineExpired = true;
                        linked.Cancel();
                        ObserveAfterCancellation(send);
                        MarkOutboxWaitExpired(localId);
                        Mutate(state => state with
                        {
                            Connection = new ConnectionState(ConnectionStatus.Offline, "send_timeout")
                        });
                        throw new GatewayException(
                            GatewayErrorKind.Offline,
                            GatewayErrorCode.RequestTimedOut,
                            innerException: new TimeoutException("The send result was not known before the deadline."));
                    }
                    deadlineCancellation.Cancel();
                }
                result = await send.ConfigureAwait(false);
            }
            catch (GatewayException) when (deadlineExpired)
            {
                throw;
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                MarkOutboxFailed(localId, OutboxFailureKind.ReauthenticationRequired);
                throw;
            }
            catch (GatewayException exception)
            {
                MarkOutboxFailed(localId, MapSendFailure(exception));
                if (IsNetwork(exception))
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline, "send_failed") });
                }
                else if (IsRateLimited(exception))
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.RateLimited, "send_rate_limited") });
                }
                throw;
            }
            catch (OperationCanceledException)
            {
                MarkOutboxFailed(localId, OutboxFailureKind.NetworkResultUnknown);
                throw;
            }

            if (!string.Equals(result.LocalId, localId, StringComparison.Ordinal))
            {
                MarkOutboxFailed(localId, OutboxFailureKind.Protocol);
                throw new InvalidOperationException("The send response local id did not match the request.");
            }

            try
            {
                var historyTask = _gateway.GetHistoryAsync(
                    new HistoryRequest(credentials, conversation, result.MessageId, includeAnchor: true, limit: 1),
                    linked.Token);
                if (!historyTask.IsCompleted)
                {
                    using var deadlineCancellation = new CancellationTokenSource();
                    var deadline = _sendDeadlineDelay(OutboxTimingPolicy.ExpiryDuration, deadlineCancellation.Token);
                    var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                    var completed = await Task.WhenAny(historyTask, deadline, cancelled).ConfigureAwait(false);
                    if (completed == cancelled)
                    {
                        deadlineCancellation.Cancel();
                        ObserveAfterCancellation(historyTask);
                        cancellationToken.ThrowIfCancellationRequested();
                        runToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException(linked.Token);
                    }
                    if (completed == deadline && !historyTask.IsCompleted)
                    {
                        await deadline.ConfigureAwait(false);
                        linked.Cancel();
                        ObserveAfterCancellation(historyTask);
                        return;
                    }
                    deadlineCancellation.Cancel();
                }
                var history = await historyTask.ConfigureAwait(false);
                var matching = history.Messages.FirstOrDefault(message => message.Id == result.MessageId);
                if (matching is not null)
                {
                    var upsert = new MessageUpsertEvent(matching, Source: DomainEventSource.Send, LocalId: localId);
                    await StoreThenApplyAsync([upsert], linked.Token).ConfigureAwait(false);
                }
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
            }
            catch (GatewayException)
            {
                // The POST succeeded. Keep the outbox item for realtime reconciliation; never resend.
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    public Task SetReactionAsync(
        long messageId,
        EmojiReactionIdentity reaction,
        bool add,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        return ExecuteMessageMutationAsync(
            messageId,
            MessageMutationKind.Reaction,
            requireOwnership: false,
            async (credentials, message, token) =>
            {
                await _gateway.SetReactionAsync(
                    new SetReactionRequest(credentials, messageId, reaction, add), token).ConfigureAwait(false);
                var fullName = State.Users.GetValueOrDefault(credentials.UserId)?.FullName;
                return new MessageReactionChangedEvent(
                    messageId,
                    new EmojiReaction(reaction, credentials.UserId, fullName),
                    add,
                    Source: DomainEventSource.Local);
            },
            cancellationToken);
    }

    public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return ExecuteMessageMutationAsync(
            messageId,
            MessageMutationKind.Edit,
            requireOwnership: true,
            async (credentials, message, token) =>
            {
                ValidateMessageContent(content);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Content)))
                    .ToLowerInvariant();
                await _gateway.EditMessageAsync(
                    new EditMessageRequest(credentials, messageId, content, hash), token).ConfigureAwait(false);
                return new MessageContentChangedEvent(messageId, content, Source: DomainEventSource.Local);
            },
            cancellationToken);
    }

    public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default) =>
        ExecuteMessageMutationAsync(
            messageId,
            MessageMutationKind.Delete,
            requireOwnership: true,
            async (credentials, _, token) =>
            {
                await _gateway.DeleteMessageAsync(
                    new DeleteMessageRequest(credentials, messageId), token).ConfigureAwait(false);
                return new MessageDeletedEvent([messageId], Source: DomainEventSource.Local);
            },
            cancellationToken);

    public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default) =>
        ExecuteMessageMutationAsync(
            messageId,
            MessageMutationKind.Star,
            requireOwnership: false,
            async (credentials, _, token) =>
            {
                await _gateway.SetMessageStarredAsync(
                    new SetMessageStarredRequest(credentials, messageId, isStarred), token).ConfigureAwait(false);
                return new MessageFlagsChangedEvent(
                    [messageId],
                    false,
                    isStarred ? MessageFlagOperation.Add : MessageFlagOperation.Remove,
                    "starred",
                    Source: DomainEventSource.Local);
            },
            cancellationToken);

    public async Task<UploadedAttachment> UploadAttachmentAsync(
        AttachmentUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidateAttachmentUpload(upload);
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credentials = GetConnectedCredentials();
            CancellationToken runToken;
            lock (_stateGate)
            {
                runToken = _runCancellation?.Token ?? throw new InvalidOperationException("The session is stopped.");
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
            try
            {
                return await _gateway.UploadAttachmentAsync(
                    new UploadAttachmentRequest(credentials, upload),
                    linked.Token).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception) when (IsNetwork(exception))
            {
                Mutate(state => state with
                {
                    Connection = new ConnectionState(ConnectionStatus.Offline, "attachment_upload_unknown")
                });
                throw;
            }
            catch (GatewayException exception) when (IsRateLimited(exception))
            {
                Mutate(state => state with
                {
                    Connection = new ConnectionState(ConnectionStatus.RateLimited, "attachment_upload_rate_limited")
                });
                throw;
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task<RealmMediaResult> GetRealmMediaAsync(
        RealmMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        ThrowIfDisposed();
        CredentialEnvelope credentials;
        lock (_stateGate)
        {
            credentials = _credentials ?? throw new InvalidOperationException("No credentials are available.");
        }
        try
        {
            return await _gateway.GetRealmMediaAsync(
                new GetRealmMediaRequest(credentials, request),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default) =>
        UnsubscribeChannelCoreAsync(channelId, allowConfirmedOwnerExit: false, cancellationToken);

    private async Task UnsubscribeChannelCoreAsync(
        long channelId,
        bool allowConfirmedOwnerExit,
        CancellationToken cancellationToken)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        var lane = _channelUnsubscribeLanes.GetOrAdd(channelId, static _ => new SemaphoreSlim(1, 1));
        await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CredentialEnvelope credentials;
            Subscription? subscription;
            AccountId accountId;
            CancellationToken runToken;
            lock (_stateGate)
            {
                if (_state.Connection.Status != ConnectionStatus.Connected)
                {
                    throw new InvalidOperationException("Channel unsubscribe requires a connected session.");
                }
                credentials = _credentials ?? throw new InvalidOperationException("No credentials are available.");
                accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                subscription = _state.Subscriptions.GetValueOrDefault(channelId);
                runToken = _runCancellation?.Token ?? throw new InvalidOperationException("The session is stopped.");
            }
            if (subscription is null) return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
            if (!allowConfirmedOwnerExit && PrivateGroupPolicy.IsEligible(subscription))
            {
                ChannelDetails details;
                try
                {
                    details = await _gateway.GetChannelDetailsAsync(
                        new ChannelDetailsRequest(credentials, channelId),
                        linked.Token).ConfigureAwait(false);
                }
                catch (GatewayException exception) when (IsUnauthorized(exception))
                {
                    await HandleUnauthorizedAsync().ConfigureAwait(false);
                    throw;
                }

                if (details.ChannelId != channelId || !PrivateGroupPolicy.IsEligible(details))
                    throw new InvalidOperationException("Refresh the private group before leaving it.");
                if (PrivateGroupPolicy.TryGetOwnerId(details) == CurrentUserId)
                    throw new InvalidOperationException("The group owner must transfer ownership or dissolve the group before leaving.");
            }

            UnsubscribeChannelResult result;
            try
            {
                result = await _gateway.UnsubscribeChannelAsync(
                    new UnsubscribeChannelRequest(credentials, subscription.Name),
                    linked.Token).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception) when (IsNetwork(exception))
            {
                Mutate(state => state with
                {
                    Connection = new ConnectionState(ConnectionStatus.Offline, "channel_unsubscribe_unknown")
                });
                throw;
            }
            catch (GatewayException exception) when (IsRateLimited(exception))
            {
                Mutate(state => state with
                {
                    Connection = new ConnectionState(ConnectionStatus.RateLimited, "channel_unsubscribe_rate_limited")
                });
                throw;
            }

            var confirmed = result.Removed.Contains(subscription.Name, StringComparer.Ordinal) ||
                result.NotRemoved.Contains(subscription.Name, StringComparer.Ordinal);
            if (!confirmed)
            {
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
            }

            lock (_stateGate)
            {
                if (_selectedConversation is ChannelTopic selected && selected.ChannelId == channelId)
                {
                    _selectedConversation = null;
                    InvalidateHistoryLocked(clearConversation: true);
                }
            }
            Mutate(state => DomainReducer.Apply(
                state,
                new SubscriptionRemovedEvent(channelId, Source: DomainEventSource.Local)));
            try
            {
                await _store.PurgeSubscriptionAsync(accountId, channelId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                Mutate(state => state with
                {
                    Connection = new ConnectionState(
                        ConnectionStatus.Faulted,
                        "channel_unsubscribe_cache_cleanup_failed")
                });
                throw;
            }
        }
        finally
        {
            lane.Release();
        }
    }

    public async Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CredentialEnvelope credentials;
        AccountId accountId;
        long generation;
        CancellationTokenSource queryCancellation;
        lock (_stateGate)
        {
            credentials = _state.Connection.Status == ConnectionStatus.Connected
                ? _credentials ?? throw new InvalidOperationException("No credentials are available.")
                : throw new InvalidOperationException("Channel discovery requires a connected session.");
            accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            _channelCatalogCancellation?.Cancel();
            _channelCatalogCancellation?.Dispose();
            queryCancellation = _channelCatalogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCancellation?.Token ?? _disposeCancellation.Token);
            generation = ++_channelCatalogGeneration;
        }
        try
        {
            var channels = await _gateway.GetAvailableChannelsAsync(new AvailableChannelsRequest(credentials), queryCancellation.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (!IsChannelCatalogCurrentLocked(accountId, generation, queryCancellation)) return [];
                channels = channels.Select(channel => _state.Subscriptions.TryGetValue(channel.ChannelId, out var subscription)
                    ? channel with { IsSubscribed = true, Color = subscription.Color }
                    : channel with { IsSubscribed = false, Color = null }).ToArray();
                _availableChannels = channels.ToDictionary(channel => channel.ChannelId);
            }
            return channels;
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsChannelCatalogCurrent(accountId, generation, queryCancellation)) await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }
        finally { }
    }

    public async Task<ChannelSettingsSnapshot> LoadChannelSettingsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CredentialEnvelope credentials;
        AccountId accountId;
        long generation;
        CancellationTokenSource queryCancellation;
        lock (_stateGate)
        {
            credentials = _state.Connection.Status == ConnectionStatus.Connected ? _credentials ?? throw new InvalidOperationException("No credentials are available.") : throw new InvalidOperationException("Channel settings require a connected session.");
            accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            _channelSettingsCancellation?.Cancel();
            _channelSettingsCancellation?.Dispose();
            queryCancellation = _channelSettingsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCancellation?.Token ?? _disposeCancellation.Token);
            generation = ++_channelSettingsGeneration;
        }
        try
        {
            var snapshot = await _gateway.GetChannelSettingsSnapshotAsync(new ChannelSettingsSnapshotRequest(credentials, _channelSettingsLimits), queryCancellation.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (!IsChannelSettingsCurrentLocked(accountId, generation, queryCancellation)) throw new OperationCanceledException(queryCancellation.Token);
                var subscriptions = _state.Subscriptions;
                var channels = snapshot.Channels.Select(channel => channel.IsSubscribed && subscriptions.TryGetValue(channel.ChannelId, out var subscription)
                    ? channel with { Color = subscription.Color }
                    : channel).ToArray();
                _channelSettingsSnapshot = snapshot with { Channels = channels };
                _availableChannels = channels.ToDictionary(channel => channel.ChannelId);
                return _channelSettingsSnapshot;
            }
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsChannelSettingsCurrent(accountId, generation, queryCancellation)) await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SetTopicVisibilityPolicyAsync(ChannelTopic topic, TopicVisibilityPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        ThrowIfDisposed();
        try { await _gateway.SetTopicVisibilityPolicyAsync(new SetTopicVisibilityPolicyRequest(GetConnectedCredentials(), topic, policy), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        lock (_stateGate)
        {
            var policies = new Dictionary<string, TopicVisibilityPolicy>(_topicVisibilityPolicies, StringComparer.Ordinal) { [topic.CanonicalKey] = policy };
            _topicVisibilityPolicies = policies;
        }
    }

    public async Task MarkTopicReadAsync(ChannelTopic topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ThrowIfDisposed();
        var credentials = GetConnectedCredentials();
        long? anchor = null;
        while (true)
        {
            TopicReadResult result;
            try { result = await _gateway.MarkTopicReadAsync(new MarkTopicReadRequest(credentials, topic, anchor), cancellationToken).ConfigureAwait(false); }
            catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
            if (result.FoundNewest) return;
            if (result.LastProcessedMessageId is not > 0 || result.LastProcessedMessageId == anchor)
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
            anchor = result.LastProcessedMessageId;
        }
    }

    public async Task MoveTopicAsync(ChannelTopic source, ChannelTopic destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source == destination) return;
        ThrowIfDisposed();
        var credentials = GetConnectedCredentials();
        TopicAnchorResult anchor;
        try { anchor = await _gateway.ResolveTopicAnchorAsync(new ResolveTopicAnchorRequest(credentials, source), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        if (anchor.MessageId is not > 0) throw new InvalidOperationException("The topic has no message anchor.");
        try { await _gateway.MoveTopicAsync(new MoveTopicRequest(credentials, source, anchor.MessageId.Value, destination), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
    }

    public Task SetTopicResolvedAsync(ChannelTopic topic, bool isResolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var name = isResolved ? TopicResolution.Resolve(topic.Topic) : TopicResolution.Unresolve(topic.Topic);
        return string.Equals(name, topic.Topic, StringComparison.Ordinal) ? Task.CompletedTask : MoveTopicAsync(topic, new ChannelTopic(topic.ChannelId, name), cancellationToken);
    }

    public async Task<TopicDeleteResult> DeleteTopicAsync(ChannelTopic topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ThrowIfDisposed();
        if (!IsOrganizationAdministrator) throw new InvalidOperationException("Deleting a topic requires an organization administrator.");
        try { return await _gateway.DeleteTopicAsync(new DeleteTopicRequest(GetConnectedCredentials(), topic), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
    }

    public async Task<ChannelDetails> LoadChannelDetailsAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        CredentialEnvelope credentials;
        lock (_stateGate) credentials = _state.Connection.Status == ConnectionStatus.Connected ? _credentials ?? throw new InvalidOperationException("No credentials are available.") : throw new InvalidOperationException("Channel settings require a connected session.");
        try { return await _gateway.GetChannelDetailsAsync(new ChannelDetailsRequest(credentials, channelId), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
    }

    public async Task UpdateChannelAsync(long channelId, string? name, string? description, long? folderId, bool clearFolder = false, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        if (PrivateGroupPolicy.IsEligible(State.Subscriptions.GetValueOrDefault(channelId)))
            await EnsurePrivateGroupOwnerAsync(channelId, cancellationToken).ConfigureAwait(false);
        var credentials = GetConnectedCredentials();
        try { await _gateway.UpdateChannelAsync(new UpdateChannelRequest(credentials, channelId, name, description, folderId, clearFolder), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        if (!string.IsNullOrWhiteSpace(name)) await StoreThenApplyAsync([new SubscriptionPatchedEvent(channelId, name.Trim(), null, Source: DomainEventSource.Local)], cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelFolder> CreateChannelFolderAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ChannelFolder folder;
        try { folder = await _gateway.CreateChannelFolderAsync(new CreateChannelFolderRequest(GetConnectedCredentials(), name, description), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        return folder;
    }

    public async Task<string> GetChannelEmailAddressAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        try { return await _gateway.GetChannelEmailAddressAsync(new ChannelEmailAddressRequest(GetConnectedCredentials(), channelId), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
    }

    public async Task ArchiveChannelAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        try { await _gateway.ArchiveChannelAsync(new ArchiveChannelRequest(GetConnectedCredentials(), channelId), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        await StoreThenApplyAsync([new SubscriptionPatchedEvent(channelId, null, false, Source: DomainEventSource.Local)], cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelSummary> CreateChannelAsync(ChannelCreateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ThrowIfDisposed();
        if (!IsOrganizationAdministrator) throw new InvalidOperationException("Creating a channel requires an organization administrator.");
        var credentials = GetConnectedCredentials();
        long channelId;
        try { channelId = await _gateway.CreateChannelAsync(new CreateChannelRequest(credentials, options), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        return new ChannelSummary(channelId, options.Name.Trim(), options.Description, false, 1, options.IsPrivate, true);
    }

    public async Task<PrivateGroupCreated> CreatePrivateGroupAsync(
        PrivateGroupCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentNullException.ThrowIfNull(options.OtherMemberIds);
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentUserId = CurrentUserId ?? throw new InvalidOperationException("No current user is available.");
            var otherMemberIds = options.OtherMemberIds
                .Where(id => id > 0 && id != currentUserId)
                .Distinct()
                .OrderBy(static id => id)
                .ToArray();
            if (otherMemberIds.Length < 2 || otherMemberIds.Length != options.OtherMemberIds.Count)
                throw new ArgumentException("A private group requires at least two distinct other members.", nameof(options));

            var state = State;
            if (otherMemberIds.Any(id => !state.Users.TryGetValue(id, out var user) || !user.IsActive))
                throw new InvalidOperationException("Refresh the active user directory before creating this group.");

            var normalizedOptions = new PrivateGroupCreateOptions(options.Name.Trim(), otherMemberIds);
            var credentials = GetConnectedCredentials();
            long channelId;
            try
            {
                channelId = await _gateway.CreatePrivateGroupAsync(
                    new PrivateGroupCreateRequest(credentials, normalizedOptions),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception) when (IsMutationResultUncertain(exception))
            {
                var refreshed = await TryRefreshRegisterSnapshotAsync(credentials, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    refreshed
                        ? "群聊创建结果无法确认；已刷新权威会话列表，请先检查是否已创建，勿直接重试。"
                        : "群聊创建结果无法确认，且暂时无法刷新权威会话列表；请恢复连接后先检查，勿直接重试。",
                    exception);
            }

            var subscription = new Subscription(
                channelId,
                normalizedOptions.Name,
                isPrivate: true,
                topicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly,
                isWebPublic: false);
            await StoreThenApplyAsync(
                [new SubscriptionChangedEvent(subscription, false, Source: DomainEventSource.Local)],
                cancellationToken).ConfigureAwait(false);
            return new PrivateGroupCreated(
                channelId,
                normalizedOptions.Name,
                new ChannelTopic(channelId, string.Empty),
                otherMemberIds.Length + 1);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task<ChannelPersonalSettings> GetChannelPersonalSettingsAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        try { return await _gateway.GetChannelPersonalSettingsAsync(new ChannelMembersRequest(GetConnectedCredentials(), channelId), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
    }

    public async Task SetChannelPersonalSettingAsync(long channelId, ChannelPersonalSettingChange change, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        CredentialEnvelope credentials;
        AccountId accountId;
        long generation;
        CancellationTokenSource runCancellation;
        lock (_stateGate)
        {
            credentials = _state.Connection.Status == ConnectionStatus.Connected
                ? _credentials ?? throw new InvalidOperationException("No credentials are available.")
                : throw new InvalidOperationException("Channel personal settings require a connected session.");
            accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            generation = _queryEpoch;
            runCancellation = _runCancellation ?? throw new InvalidOperationException("The session is stopped.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCancellation.Token);
        try { await _gateway.SetChannelPersonalSettingAsync(new SetChannelPersonalSettingRequest(credentials, channelId, change), linked.Token).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsChannelOperationCurrent(accountId, generation, runCancellation)) await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }

        if (change.Setting != ChannelPersonalSetting.Color || string.IsNullOrWhiteSpace(change.ColorValue)) return;
        var changed = false;
        lock (_stateGate)
        {
            if (!IsChannelOperationCurrentLocked(accountId, generation, runCancellation) ||
                !_state.Subscriptions.TryGetValue(channelId, out var subscription) ||
                string.Equals(subscription.Color, change.ColorValue, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _state = _state with
            {
                Subscriptions = new Dictionary<long, Subscription>(_state.Subscriptions)
                {
                    [channelId] = subscription with { Color = change.ColorValue }
                }
            };
            changed = true;
        }
        if (changed) RaiseStateChanged();
    }

    public async Task<IReadOnlyList<long>> GetChannelMemberIdsAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        try { return await _gateway.GetChannelMemberIdsAsync(new ChannelMembersRequest(GetConnectedCredentials(), channelId), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<UserProfile>> GetRealmUsersAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try { return await _gateway.GetRealmUsersAsync(new RealmUsersRequest(GetConnectedCredentials()), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
    }

    public Task AddChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, bool sendNewSubscriptionMessages, CancellationToken cancellationToken = default) =>
        ModifyChannelMembersAsync(channelId, principalIds, true, sendNewSubscriptionMessages, cancellationToken);

    public Task RemoveChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, CancellationToken cancellationToken = default) =>
        ModifyChannelMembersAsync(channelId, principalIds, false, false, cancellationToken);

    public async Task UpdateChannelAdvancedSettingsAsync(long channelId, ChannelAdvancedSettingsChange change, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        try { await _gateway.UpdateChannelAdvancedSettingsAsync(new UpdateChannelAdvancedRequest(GetConnectedCredentials(), channelId, change), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        await LoadChannelSettingsSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrivateGroupTransferResult> TransferPrivateGroupOwnershipAsync(
        long channelId,
        long newOwnerId,
        CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        if (newOwnerId <= 0) throw new ArgumentOutOfRangeException(nameof(newOwnerId));
        ThrowIfDisposed();
        var currentUserId = CurrentUserId ?? throw new InvalidOperationException("No current user is available.");
        if (newOwnerId == currentUserId) throw new InvalidOperationException("The selected member already owns this group.");

        var (credentials, details, memberIds) = await LoadPrivateGroupAuthorityAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (PrivateGroupPolicy.TryGetOwnerId(details) != currentUserId)
            throw new InvalidOperationException("Only the confirmed RelayCove group owner can transfer ownership.");
        if (!memberIds.Contains(newOwnerId) ||
            !State.Users.TryGetValue(newOwnerId, out var newOwner) ||
            !newOwner.IsActive ||
            newOwner.IsBot)
        {
            throw new InvalidOperationException("Choose an active non-bot member of this group.");
        }

        var replacement = PrivateGroupPolicy.OwnerGroup(newOwnerId);
        var updates = new[]
        {
            new ChannelGroupSettingUpdate(ChannelGroupSettingName.CanAdministerChannel, replacement, details.CanAdministerChannelGroup!),
            new ChannelGroupSettingUpdate(ChannelGroupSettingName.CanAddSubscribers, replacement, details.CanAddSubscribersGroup!),
            new ChannelGroupSettingUpdate(ChannelGroupSettingName.CanRemoveSubscribers, replacement, details.CanRemoveSubscribersGroup!)
        };
        try
        {
            await _gateway.UpdateChannelAdvancedSettingsAsync(
                new UpdateChannelAdvancedRequest(credentials, channelId, new ChannelAdvancedSettingsChange(GroupSettings: updates)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }
        catch (GatewayException exception) when (IsMutationResultUncertain(exception))
        {
            var refreshedOwnerId = await TryRefreshPrivateGroupOwnerAsync(credentials, channelId, cancellationToken).ConfigureAwait(false);
            return refreshedOwnerId == newOwnerId
                ? new PrivateGroupTransferResult(true, false, "群主已转让，但请求结果曾不确定，原群主尚未退出。")
                : new PrivateGroupTransferResult(false, false, "群主转让结果无法确认；未退出群聊，请刷新后人工确认。" );
        }

        var confirmedOwnerId = await TryRefreshPrivateGroupOwnerAsync(credentials, channelId, cancellationToken).ConfigureAwait(false);
        if (confirmedOwnerId != newOwnerId)
            return new PrivateGroupTransferResult(false, false, "服务器尚未确认新群主；原群主未退出。" );

        try
        {
            await UnsubscribeChannelCoreAsync(channelId, allowConfirmedOwnerExit: false, cancellationToken).ConfigureAwait(false);
            return new PrivateGroupTransferResult(true, true, "群主已转让，原群主已退出群聊。" );
        }
        catch (OperationCanceledException)
        {
            return new PrivateGroupTransferResult(true, false, "群主已转让，但退出操作已取消；原群主仍在群内。" );
        }
        catch
        {
            return new PrivateGroupTransferResult(true, false, "群主已转让，但原群主尚未退出，请稍后只重试退出。" );
        }
    }

    public async Task<PrivateGroupDissolveResult> DissolvePrivateGroupAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        var currentUserId = CurrentUserId ?? throw new InvalidOperationException("No current user is available.");
        var (credentials, details, memberIds) = await LoadPrivateGroupAuthorityAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (PrivateGroupPolicy.TryGetOwnerId(details) != currentUserId)
            throw new InvalidOperationException("Only the confirmed RelayCove group owner can dissolve this group.");

        var otherMemberIds = memberIds.Where(id => id != currentUserId).Distinct().OrderBy(static id => id).ToArray();
        if (otherMemberIds.Length > 0)
        {
            try
            {
                await _gateway.ModifyChannelMembersAsync(
                    new ModifyChannelMembersRequest(credentials, details.Name, otherMemberIds, false, false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception) when (IsMutationResultUncertain(exception))
            {
                var refreshed = await TryGetChannelMemberIdsAsync(credentials, channelId, cancellationToken).ConfigureAwait(false);
                return refreshed is not null && refreshed.All(id => id == currentUserId)
                    ? new PrivateGroupDissolveResult(true, false, "其他成员已移除，但请求结果曾不确定；群主尚未退出。")
                    : new PrivateGroupDissolveResult(false, false, "移除成员的结果无法确认；群主未退出，请刷新后人工确认。" );
            }
        }

        var confirmedMembers = await TryGetChannelMemberIdsAsync(credentials, channelId, cancellationToken).ConfigureAwait(false);
        if (confirmedMembers is null || confirmedMembers.Count != 1 || confirmedMembers[0] != currentUserId)
            return new PrivateGroupDissolveResult(false, false, "仍有其他成员或出现并发变更；已停止解散，群主未退出。" );

        try
        {
            await UnsubscribeChannelCoreAsync(channelId, allowConfirmedOwnerExit: true, cancellationToken).ConfigureAwait(false);
            return new PrivateGroupDissolveResult(true, true, "所有成员已退出，群聊已从 RelayCove 列表移除；服务器私有历史未删除。" );
        }
        catch (OperationCanceledException)
        {
            return new PrivateGroupDissolveResult(true, false, "其他成员已移除，但群主退出已取消；服务器私有历史未删除。" );
        }
        catch
        {
            return new PrivateGroupDissolveResult(true, false, "其他成员已移除，但群主尚未退出；不要重复移除成员，只需重试退出。" );
        }
    }

    public Task UnarchiveChannelAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (!IsOrganizationAdministrator) return Task.FromException(new InvalidOperationException("Unarchiving a channel requires an organization administrator."));
        return UpdateChannelAdvancedSettingsAsync(channelId, new ChannelAdvancedSettingsChange(IsArchived: false), cancellationToken);
    }

    private async Task<(CredentialEnvelope Credentials, ChannelDetails Details, IReadOnlyList<long> MemberIds)> LoadPrivateGroupAuthorityAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        var (credentials, details) = await LoadPrivateGroupDetailsAsync(channelId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<long> memberIds;
        try
        {
            memberIds = await _gateway.GetChannelMemberIdsAsync(
                new ChannelMembersRequest(credentials, channelId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }

        if (memberIds.Count == 0 || memberIds.Any(id => id <= 0) || memberIds.Distinct().Count() != memberIds.Count ||
            CurrentUserId is not { } currentUserId || !memberIds.Contains(currentUserId))
        {
            throw new InvalidOperationException("Refresh the authoritative member list before managing this group.");
        }

        return (credentials, details, memberIds);
    }

    private async Task<(CredentialEnvelope Credentials, ChannelDetails Details)> LoadPrivateGroupDetailsAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        if (!PrivateGroupPolicy.IsEligible(State.Subscriptions.GetValueOrDefault(channelId)))
            throw new InvalidOperationException("Refresh subscriptions before managing this private group.");

        var credentials = GetConnectedCredentials();
        ChannelDetails details;
        try
        {
            details = await _gateway.GetChannelDetailsAsync(
                new ChannelDetailsRequest(credentials, channelId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            await HandleUnauthorizedAsync().ConfigureAwait(false);
            throw;
        }

        if (details.ChannelId != channelId || !PrivateGroupPolicy.IsEligible(details) || string.IsNullOrWhiteSpace(details.Name))
            throw new InvalidOperationException("This channel is no longer an eligible RelayCove private group.");
        return (credentials, details);
    }

    private async Task EnsurePrivateGroupOwnerAsync(long channelId, CancellationToken cancellationToken)
    {
        var (_, details) = await LoadPrivateGroupDetailsAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (PrivateGroupPolicy.TryGetOwnerId(details) != CurrentUserId)
            throw new InvalidOperationException("Only the confirmed RelayCove group owner can manage this group.");
    }

    private async Task<long?> TryRefreshPrivateGroupOwnerAsync(
        CredentialEnvelope credentials,
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var details = await _gateway.GetChannelDetailsAsync(
                new ChannelDetailsRequest(credentials, channelId),
                cancellationToken).ConfigureAwait(false);
            return details.ChannelId == channelId ? PrivateGroupPolicy.TryGetOwnerId(details) : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<long>?> TryGetChannelMemberIdsAsync(
        CredentialEnvelope credentials,
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _gateway.GetChannelMemberIdsAsync(
                new ChannelMembersRequest(credentials, channelId),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task ModifyChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, bool add, bool sendNewSubscriptionMessages, CancellationToken cancellationToken)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentNullException.ThrowIfNull(principalIds);
        ThrowIfDisposed();
        var requiresPrivateGroupOwner = PrivateGroupPolicy.IsEligible(State.Subscriptions.GetValueOrDefault(channelId));
        var credentials = GetConnectedCredentials();
        ChannelDetails details;
        try { details = await _gateway.GetChannelDetailsAsync(new ChannelDetailsRequest(credentials, channelId), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        if (details.ChannelId != channelId || details.IsArchived || string.IsNullOrWhiteSpace(details.Name))
            throw new InvalidOperationException("Refresh channel settings before changing members.");
        if (requiresPrivateGroupOwner &&
            (!PrivateGroupPolicy.IsEligible(details) || PrivateGroupPolicy.TryGetOwnerId(details) != CurrentUserId))
        {
            throw new InvalidOperationException("Only the confirmed RelayCove group owner can change group members.");
        }
        try { await _gateway.ModifyChannelMembersAsync(new ModifyChannelMembersRequest(credentials, details.Name, principalIds, add, sendNewSubscriptionMessages), cancellationToken).ConfigureAwait(false); }
        catch (GatewayException exception) when (IsUnauthorized(exception)) { await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
        catch (GatewayException exception) when (IsMutationResultUncertain(exception))
        {
            var refreshed = await TryGetChannelMemberIdsAsync(credentials, channelId, cancellationToken).ConfigureAwait(false);
            var confirmed = refreshed is not null && (add
                ? principalIds.All(refreshed.Contains)
                : principalIds.All(id => !refreshed.Contains(id)));
            throw new InvalidOperationException(
                confirmed
                    ? "成员变更已由权威成员列表确认，但原请求结果曾不确定；请刷新群设置，勿重复操作。"
                    : "成员变更结果无法确认；已尝试刷新权威成员列表，请先核对当前成员，勿直接重试。",
                exception);
        }
    }

    private async Task<bool> TryRefreshRegisterSnapshotAsync(
        CredentialEnvelope credentials,
        CancellationToken cancellationToken)
    {
        var expectedAccountId = RelayCove.Core.AccountId.Create(credentials.Realm, credentials.UserId);
        await StopRunAsync(setOffline: false).ConfigureAwait(false);
        try
        {
            var register = await _gateway.RegisterAsync(
                new RegisterRequest(credentials),
                cancellationToken).ConfigureAwait(false);
            return await ApplyRegisterAsync(
                register,
                cancellationToken,
                credentials,
                expectedAccountId).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            await HandleUnauthorizedAsync().ConfigureAwait(false);
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            var shouldRestart = false;
            lock (_stateGate)
            {
                shouldRestart = _credentials is not null && _runCancellation is null;
            }
            if (shouldRestart) StartRun();
        }
    }

    public async Task SubscribeToChannelAsync(long channelId, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ThrowIfDisposed();
        var lane = _channelSubscribeLanes.GetOrAdd(channelId, static _ => new SemaphoreSlim(1, 1));
        await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialEnvelope credentials;
            AccountId accountId;
            long generation;
            CancellationTokenSource runCancellation;
            ChannelSummary channel;
            lock (_stateGate)
            {
                credentials = _state.Connection.Status == ConnectionStatus.Connected ? _credentials ?? throw new InvalidOperationException("No credentials are available.") : throw new InvalidOperationException("Channel subscription requires a connected session.");
                channel = _availableChannels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("Refresh available channels before subscribing.");
                if (channel.IsArchived) throw new InvalidOperationException("Archived channels cannot be joined.");
                accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                generation = _queryEpoch;
                runCancellation = _runCancellation ?? throw new InvalidOperationException("The session is stopped.");
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCancellation.Token);
            IReadOnlyList<ChannelSummary> refreshed;
            try { refreshed = await _gateway.GetAvailableChannelsAsync(new AvailableChannelsRequest(credentials), linked.Token).ConfigureAwait(false); }
            catch (GatewayException exception) when (IsUnauthorized(exception)) { if (IsChannelOperationCurrent(accountId, generation, runCancellation)) await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
            if (!IsChannelOperationCurrent(accountId, generation, runCancellation)) return;
            var current = refreshed.FirstOrDefault(item => item.ChannelId == channelId);
            if (current is null || !string.Equals(current.Name, channel.Name, StringComparison.Ordinal) || current.IsArchived)
                throw new InvalidOperationException("The available-channel catalog changed; refresh before joining.");
            lock (_stateGate) { if (!IsChannelOperationCurrentLocked(accountId, generation, runCancellation)) return; _availableChannels = refreshed.ToDictionary(item => item.ChannelId); }
            SubscribeChannelResult result;
            try { result = await _gateway.SubscribeToChannelAsync(new SubscribeChannelRequest(credentials, current), linked.Token).ConfigureAwait(false); }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                if (IsChannelOperationCurrent(accountId, generation, runCancellation)) await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            if (!IsChannelOperationCurrent(accountId, generation, runCancellation)) return;
            if (result.Unauthorized.Contains(current.Name, StringComparer.Ordinal) || !result.Confirms(current.Name))
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
            await StoreThenApplyAsync([new SubscriptionChangedEvent(new Subscription(current.ChannelId, current.Name), false, Source: DomainEventSource.Local)], linked.Token).ConfigureAwait(false);
        }
        finally { lane.Release(); }
    }

    public async Task SetSubscriptionPreferenceAsync(long channelId, SubscriptionPreference preference, bool value, CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        var lane = _channelPreferenceLanes.GetOrAdd(channelId, static _ => new SemaphoreSlim(1, 1));
        await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialEnvelope credentials;
            AccountId accountId;
            long generation;
            CancellationTokenSource runCancellation;
            Subscription subscription;
            lock (_stateGate)
            {
                credentials = _state.Connection.Status == ConnectionStatus.Connected ? _credentials ?? throw new InvalidOperationException("No credentials are available.") : throw new InvalidOperationException("Subscription preferences require a connected session.");
                subscription = _state.Subscriptions.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("The channel is not subscribed.");
                accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                generation = _queryEpoch;
                runCancellation = _runCancellation ?? throw new InvalidOperationException("The session is stopped.");
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCancellation.Token);
            try { await _gateway.SetSubscriptionPreferenceAsync(new SetSubscriptionPreferenceRequest(credentials, channelId, preference, value), linked.Token).ConfigureAwait(false); }
            catch (GatewayException exception) when (IsUnauthorized(exception)) { if (IsChannelOperationCurrent(accountId, generation, runCancellation)) await HandleUnauthorizedAsync().ConfigureAwait(false); throw; }
            if (!IsChannelOperationCurrent(accountId, generation, runCancellation)) return;
            await StoreThenApplyAsync([new SubscriptionPreferenceChangedEvent(channelId, preference, value, Source: DomainEventSource.Local)], linked.Token).ConfigureAwait(false);
        }
        finally { lane.Release(); }
    }

    public async Task SetOwnPresenceAsync(
        UserPresenceStatus status,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        ThrowIfDisposed();
        await _ownPresenceLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialEnvelope credentials;
            AccountId accountId;
            long generation;
            CancellationTokenSource runCancellation;
            bool? wasEnabled;
            lock (_stateGate)
            {
                if (!_isPresenceAvailable || _isOwnPresenceEnabled is null)
                    throw new InvalidOperationException("Presence settings are not available for this account.");
                credentials = _state.Connection.Status == ConnectionStatus.Connected
                    ? _credentials ?? throw new InvalidOperationException("No credentials are available.")
                    : throw new InvalidOperationException("Presence settings require a connected session.");
                accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                generation = _queryEpoch;
                runCancellation = _runCancellation ?? throw new InvalidOperationException("The session is stopped.");
                wasEnabled = _isOwnPresenceEnabled;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                runCancellation.Token);
            try
            {
                if (status == UserPresenceStatus.Offline)
                {
                    await _gateway.SetPresenceEnabledAsync(
                        new SetPresenceEnabledRequest(credentials, false),
                        linked.Token).ConfigureAwait(false);
                }
                else
                {
                    await _gateway.UpdateOwnPresenceAsync(
                        new UpdateOwnPresenceRequest(credentials, status),
                        linked.Token).ConfigureAwait(false);
                    if (wasEnabled is false)
                    {
                        await _gateway.SetPresenceEnabledAsync(
                            new SetPresenceEnabledRequest(credentials, true),
                            linked.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                if (IsChannelOperationCurrent(accountId, generation, runCancellation))
                    await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception)
            {
                if (IsMutationResultUncertain(exception))
                {
                    MarkOwnPresenceUnconfirmedIfCurrent(
                        accountId,
                        generation,
                        runCancellation,
                        credentials);
                }
                throw;
            }
            catch (OperationCanceledException)
            {
                MarkOwnPresenceUnconfirmedIfCurrent(
                    accountId,
                    generation,
                    runCancellation,
                    credentials);
                throw;
            }

            if (!IsChannelOperationCurrent(accountId, generation, runCancellation)) return;
            var now = _utcNow();
            lock (_stateGate)
            {
                if (!IsChannelOperationCurrentLocked(accountId, generation, runCancellation)) return;
                _isOwnPresenceEnabled = status != UserPresenceStatus.Offline;
                _ownPresenceStatus = status;
            }
            Mutate(state => SetPresenceValue(
                state,
                credentials.UserId,
                status,
                now));
        }
        finally
        {
            _ownPresenceLane.Release();
        }
    }

    public async Task SetOwnUserStatusAsync(
        UserStatusContent status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ThrowIfDisposed();
        await _ownUserStatusLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialEnvelope credentials;
            AccountId accountId;
            long generation;
            long lastEventId;
            CancellationTokenSource runCancellation;
            lock (_stateGate)
            {
                if (!_isUserStatusAvailable)
                    throw new InvalidOperationException("User status settings are not available for this account.");
                credentials = _state.Connection.Status == ConnectionStatus.Connected
                    ? _credentials ?? throw new InvalidOperationException("No credentials are available.")
                    : throw new InvalidOperationException("User status settings require a connected session.");
                accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                generation = _queryEpoch;
                lastEventId = _state.LastEventId ?? 0;
                runCancellation = _runCancellation ?? throw new InvalidOperationException("The session is stopped.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                runCancellation.Token);
            try
            {
                await _gateway.UpdateOwnUserStatusAsync(
                    new UpdateOwnUserStatusRequest(credentials, status),
                    linked.Token).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                if (IsChannelOperationCurrent(accountId, generation, runCancellation))
                    await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception)
            {
                if (IsMutationResultUncertain(exception))
                    MarkOwnUserStatusUnconfirmedIfCurrent(
                        accountId,
                        generation,
                        runCancellation,
                        credentials,
                        status,
                        lastEventId);
                throw;
            }
            catch (OperationCanceledException)
            {
                MarkOwnUserStatusUnconfirmedIfCurrent(
                    accountId,
                    generation,
                    runCancellation,
                    credentials,
                    status,
                    lastEventId);
                throw;
            }

            if (!IsChannelOperationCurrent(accountId, generation, runCancellation)) return;
            lock (_stateGate)
            {
                if (!IsChannelOperationCurrentLocked(accountId, generation, runCancellation) ||
                    !EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, credentials)) return;
                _isOwnUserStatusConfirmed = true;
                _pendingOwnUserStatusConfirmation = null;
                _pendingOwnUserStatusAfterEventId = 0;
            }
            Mutate(state => DomainReducer.Apply(
                state,
                new UserStatusChangedEvent(
                    credentials.UserId,
                    status.IsEmpty ? null : status,
                    Source: DomainEventSource.Local)));
        }
        finally
        {
            _ownUserStatusLane.Release();
        }
    }

    private void MarkOwnUserStatusUnconfirmedIfCurrent(
        AccountId accountId,
        long generation,
        CancellationTokenSource runCancellation,
        CredentialEnvelope credentials,
        UserStatusContent target,
        long afterEventId)
    {
        lock (_stateGate)
        {
            if (!IsChannelOperationCurrentLocked(accountId, generation, runCancellation) ||
                !EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, credentials)) return;
            if (_lastOwnUserStatusEventId is { } eventId && eventId > afterEventId &&
                UserStatusMatches(_lastOwnUserStatusEventValue, target))
            {
                _isOwnUserStatusConfirmed = true;
                _pendingOwnUserStatusConfirmation = null;
                _pendingOwnUserStatusAfterEventId = 0;
            }
            else
            {
                _isOwnUserStatusConfirmed = false;
                _pendingOwnUserStatusConfirmation = target;
                _pendingOwnUserStatusAfterEventId = afterEventId;
            }
        }
        RaiseStateChanged();
    }

    private static bool UserStatusMatches(UserStatusContent? actual, UserStatusContent expected) =>
        expected.IsEmpty
            ? actual is null || actual.IsEmpty
            : Equals(actual, expected);

    public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) =>
        MarkDisplayedReadCoreAsync(null, cancellationToken);

    public Task MarkDisplayedReadAsync(
        ConversationKey expectedConversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedConversation);
        return MarkDisplayedReadCoreAsync(expectedConversation, cancellationToken);
    }

    private async Task MarkDisplayedReadCoreAsync(
        ConversationKey? expectedConversation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConversationKey conversation;
            lock (_stateGate)
            {
                conversation = _selectedConversation ?? throw new InvalidOperationException("No conversation is selected.");
                if (expectedConversation is not null && conversation != expectedConversation) return;
            }
            var credentials = GetConnectedCredentials();
            var displayed = State.Messages.Values
                .Where(message => message.Conversation == conversation)
                .OrderByDescending(message => message.Id)
                .Take(50)
                .ToArray();
            var unread = displayed.Where(message => !message.IsRead).ToArray();
            if (unread.Length == 0) return;
            try
            {
                await _gateway.MarkReadAsync(
                    new MarkReadRequest(credentials, conversation, unread.Max(message => message.Id), unread.Length),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            var flags = new MessageFlagsChangedEvent(
                unread.Select(message => message.Id).ToArray(), false, MessageFlagOperation.Add, "read", Source: DomainEventSource.Local);
            await StoreThenApplyAsync([flags], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task ClearLocalCacheAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopRunAsync(setOffline: false).ConfigureAwait(false);
            AccountId? accountId;
            CredentialEnvelope? credentials;
            string? queueId;
            lock (_stateGate)
            {
                accountId = _accountId;
                credentials = _credentials;
                queueId = _queueId;
                _queueId = null;
            }
            if (accountId is null) return;
            if (credentials is not null && queueId is not null)
            {
                try
                {
                    await _gateway.DeleteQueueAsync(
                        new DeleteQueueRequest(credentials, queueId), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is GatewayException or OperationCanceledException)
                {
                    // Queue cleanup is best effort; the old queue is never reused locally.
                }
            }
            await _store.ClearAsync(accountId.Value, cancellationToken).ConfigureAwait(false);
            if (credentials is not null)
            {
                await _store.InitializeAsync(ToStoredAccount(credentials), cancellationToken).ConfigureAwait(false);
                await _store.SetCacheUnlockedAsync(accountId.Value, true, cancellationToken).ConfigureAwait(false);
            }
            lock (_stateGate)
            {
                var connection = _state.Connection;
                _state = ClientState.Empty with { Connection = connection };
                _recentDirectMessages = [];
                _historyMemoryCache.Clear();
                _historyMemoryLru.Clear();
            }
            RaiseStateChanged();
            if (credentials is null) return;
            try
            {
                var register = await _gateway.RegisterAsync(
                    new RegisterRequest(credentials), cancellationToken).ConfigureAwait(false);
                await ApplyRegisterAsync(register, cancellationToken).ConfigureAwait(false);
                StartRun();
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsNetwork(exception) || IsRateLimited(exception))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline, "cache_cleared") });
                StartRun();
            }
            catch (GatewayException)
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Faulted, "register_failed_after_clear") });
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopRunAsync(setOffline: true).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _disposeCancellation.Cancel();
            await StopRunAsync(setOffline: false).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
        _disposeCancellation.Dispose();
    }

    private void StartRun()
    {
        lock (_stateGate)
        {
            CancelMessageQueriesLocked();
            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellation.Token);
            _eventLoop = RunEventLoopAsync(_runCancellation.Token);
            _presenceLoop = RunPresenceLoopAsync(_runCancellation.Token);
        }
    }

    private async Task RunEventLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var backoff = TimeSpan.FromSeconds(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CredentialEnvelope? credentials;
                string? queue;
                long cursor;
                TimeSpan timeout;
                lock (_stateGate)
                {
                    credentials = _credentials;
                    queue = _queueId;
                    cursor = _state.LastEventId ?? 0;
                    timeout = _longPollTimeout;
                }
                if (credentials is null) return;
                if (queue is null)
                {
                    var registered = await RegisterWithRetryAsync(credentials, cancellationToken).ConfigureAwait(false);
                    if (!registered) return;
                    backoff = TimeSpan.FromSeconds(1);
                    continue;
                }
                try
                {
                    var batch = await _gateway.GetEventsAsync(
                        new GetEventsRequest(credentials, queue, cursor, timeout), cancellationToken).ConfigureAwait(false);
                    var acceptedEvents = NormalizeOwnMessages(FilterRealtimeEvents(batch.Events, cursor));
                    if (acceptedEvents.Length > 0)
                    {
                        await StoreThenApplyAsync(acceptedEvents, cancellationToken).ConfigureAwait(false);
                    }
                    var serverRestarted = acceptedEvents.Any(domainEvent => domainEvent is ServerRestartedEvent);
                    var nextCursor = acceptedEvents
                        .Select(domainEvent => domainEvent.EventId ?? cursor)
                        .Append(batch.LastEventId)
                        .Append(cursor)
                        .Max();
                    Mutate(state => state with
                    {
                        LastEventId = nextCursor,
                        Connection = serverRestarted
                            ? new ConnectionState(ConnectionStatus.Reconnecting, "server_restart")
                            : new ConnectionState(ConnectionStatus.Connected)
                    });
                    if (serverRestarted)
                    {
                        lock (_stateGate) _queueId = null;
                        if (!await RecoverFromServerRestartAsync(credentials, cancellationToken).ConfigureAwait(false)) return;
                    }
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (GatewayException exception) when (IsUnauthorized(exception))
                {
                    await HandleUnauthorizedAsync().ConfigureAwait(false);
                    return;
                }
                catch (GatewayException exception) when (IsQueueExpired(exception))
                {
                    lock (_stateGate) _queueId = null;
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Reconnecting, "queue_expired") });
                }
                catch (GatewayException exception) when (IsRateLimited(exception))
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.RateLimited) });
                    await _delay(exception.RetryAfter ?? backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (GatewayException exception) when (IsNetwork(exception))
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline) });
                    await _delay(backoff, cancellationToken).ConfigureAwait(false);
                    backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
                }
                catch (GatewayException)
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Faulted) });
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Faulted, "event_loop_failed") });
        }
    }

    private async Task<bool> RegisterWithRetryAsync(CredentialEnvelope credentials, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var register = await _gateway.RegisterAsync(new RegisterRequest(credentials), cancellationToken).ConfigureAwait(false);
                await ApplyRegisterAsync(register, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                return false;
            }
            catch (GatewayException exception) when (IsRateLimited(exception))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.RateLimited) });
                await _delay(exception.RetryAfter ?? backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsNetwork(exception))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline) });
                await _delay(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
            catch (GatewayException)
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Faulted) });
                return false;
            }
        }
        return false;
    }

    private async Task<bool> RecoverFromServerRestartAsync(
        CredentialEnvelope credentials,
        CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var probe = await _gateway.ProbeRealmAsync(credentials.Realm, cancellationToken).ConfigureAwait(false);
                if (!probe.IsCompatible)
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Faulted, "incompatible_after_restart") });
                    return false;
                }
                break;
            }
            catch (GatewayException exception) when (IsRateLimited(exception))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.RateLimited) });
                await _delay(exception.RetryAfter ?? backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsNetwork(exception))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline) });
                await _delay(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
            catch (GatewayException)
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Faulted, "probe_failed_after_restart") });
                return false;
            }
        }
        await _delay(_serverRestartDelay(), cancellationToken).ConfigureAwait(false);
        return await RegisterWithRetryAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRefreshRealmPresenceAsync(
        CredentialEnvelope credentials,
        CancellationToken cancellationToken)
    {
        bool isEnabled;
        AccountId accountId;
        long generation;
        CancellationTokenSource runCancellation;
        lock (_stateGate)
        {
            if (_runCancellation is null ||
                _runCancellation.Token != cancellationToken ||
                !EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, credentials))
            {
                return false;
            }
            isEnabled = _isPresenceAvailable;
            accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            generation = _queryEpoch;
            runCancellation = _runCancellation;
        }
        if (!isEnabled) return true;

        RealmPresenceResult result;
        try
        {
            result = await _gateway.GetRealmPresenceAsync(
                new GetRealmPresenceRequest(credentials),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials))
                await HandleUnauthorizedAsync().ConfigureAwait(false);
            return false;
        }
        catch (GatewayException exception) when (IsRateLimited(exception) || IsNetwork(exception))
        {
            return IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials);
        }
        catch (GatewayException)
        {
            return IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials);
        }

        if (!IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials)) return false;

        Mutate(state =>
        {
            var userIdsByEmail = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in state.Users.Values)
            {
                if (!string.IsNullOrWhiteSpace(user.Email)) userIdsByEmail[user.Email] = user.UserId;
            }

            var presences = new Dictionary<long, UserPresence>();
            foreach (var entry in result.Presences)
            {
                if (!userIdsByEmail.TryGetValue(entry.UserEmail, out var userId)) continue;
                presences[userId] = new UserPresence(userId, entry.ActiveTimestamp, entry.IdleTimestamp);
            }
            return state with { Presence = new PresenceState(true, presences) };
        });
        return true;
    }

    private async Task<bool> TryReportOwnPresenceAsync(
        CredentialEnvelope credentials,
        CancellationToken cancellationToken)
    {
        await _ownPresenceLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UserPresenceStatus? status;
            bool? isEnabled;
            AccountId accountId;
            long generation;
            CancellationTokenSource runCancellation;
            lock (_stateGate)
            {
                if (_runCancellation is null ||
                    _runCancellation.Token != cancellationToken ||
                    !EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, credentials))
                {
                    return false;
                }
                status = _ownPresenceStatus;
                isEnabled = _isOwnPresenceEnabled;
                accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                generation = _queryEpoch;
                runCancellation = _runCancellation;
            }
            if (isEnabled is not true || status is not (UserPresenceStatus.Active or UserPresenceStatus.Idle))
                return true;

            try
            {
                await _gateway.UpdateOwnPresenceAsync(
                    new UpdateOwnPresenceRequest(credentials, status.Value),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                if (IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials))
                    await HandleUnauthorizedAsync().ConfigureAwait(false);
                return false;
            }
            catch (GatewayException exception) when (IsRateLimited(exception) || IsNetwork(exception))
            {
                return IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials);
            }
            catch (GatewayException)
            {
                return IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials);
            }

            if (!IsPresenceOperationCurrent(accountId, generation, runCancellation, credentials)) return false;
            var now = _utcNow();
            Mutate(state => SetPresenceValue(
                state,
                credentials.UserId,
                status.Value,
                now));
            return true;
        }
        finally
        {
            _ownPresenceLane.Release();
        }
    }

    private bool IsPresenceOperationCurrent(
        AccountId accountId,
        long generation,
        CancellationTokenSource runCancellation,
        CredentialEnvelope credentials)
    {
        lock (_stateGate)
        {
            return IsChannelOperationCurrentLocked(accountId, generation, runCancellation) &&
                EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, credentials);
        }
    }

    private void MarkOwnPresenceUnconfirmedIfCurrent(
        AccountId accountId,
        long generation,
        CancellationTokenSource runCancellation,
        CredentialEnvelope credentials)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (!IsChannelOperationCurrentLocked(accountId, generation, runCancellation) ||
                !EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, credentials) ||
                _ownPresenceStatus is null)
            {
                return;
            }
            _ownPresenceStatus = null;
            changed = true;
        }
        if (changed) RaiseStateChanged();
    }

    private static ClientState SetPresenceValue(
        ClientState state,
        long userId,
        UserPresenceStatus status,
        DateTimeOffset now)
    {
        if (!state.Presence.IsAvailable) return state;
        var users = new Dictionary<long, UserPresence>(state.Presence.Users)
        {
            [userId] = status switch
            {
                UserPresenceStatus.Active => new UserPresence(userId, now, now),
                UserPresenceStatus.Idle => new UserPresence(userId, null, now),
                _ => new UserPresence(userId, null, null)
            }
        };
        return state with { Presence = new PresenceState(true, users) };
    }

    private async Task<bool> ApplyRegisterAsync(
        RegisterResult register,
        CancellationToken cancellationToken,
        CredentialEnvelope? expectedCredentials = null,
        AccountId? expectedAccountId = null)
    {
        AccountId accountId;
        lock (_stateGate)
        {
            accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            if (expectedCredentials is not null &&
                (expectedAccountId != accountId ||
                 !EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, expectedCredentials)))
            {
                return false;
            }
        }
        var eligibilityState = new ClientState(subscriptions: register.Subscriptions.ToDictionary(item => item.ChannelId));
        var normalizedRegister = register with
        {
            Events = FilterConversationEventsForPersistence(
                eligibilityState,
                NormalizeOwnMessages(register.Events)),
            RecentDirectMessages = register.RecentDirectMessages
                .OfType<DirectMessage>()
                .Where(static conversation => conversation.OtherUserIds.Count <= 1)
                .Cast<ConversationKey>()
                .ToArray(),
            Unread = FilterSupportedUnread(register.Unread, eligibilityState)
        };
        await _store.ReplaceRegisterSnapshotAsync(accountId, normalizedRegister, cancellationToken).ConfigureAwait(false);
        var loaded = await _store.LoadAsync(accountId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, OutboxEntry> outbox;
        lock (_stateGate) outbox = _state.Outbox;
        var snapshot = loaded?.State ?? new ClientState(
            subscriptions: normalizedRegister.Subscriptions.ToDictionary(item => item.ChannelId),
            users: normalizedRegister.Users.ToDictionary(item => item.UserId),
            unread: normalizedRegister.Unread);
        snapshot = FilterSupportedConversations(DomainReducer.Apply(snapshot, normalizedRegister.Events) with
        {
            Outbox = new Dictionary<string, OutboxEntry>(outbox, StringComparer.Ordinal),
            Connection = new ConnectionState(ConnectionStatus.Connected),
            LastEventId = register.LastEventId,
            Presence = new PresenceState(
                normalizedRegister.IsPresenceAvailable,
                (normalizedRegister.Presences ?? []).ToDictionary(item => item.UserId)),
            UserStatuses = new UserStatusState(
                normalizedRegister.IsUserStatusAvailable,
                (normalizedRegister.UserStatuses ?? []).ToDictionary(item => item.UserId, item => item.Content))
        });
        lock (_stateGate)
        {
            if (expectedCredentials is not null &&
                (expectedAccountId != _accountId ||
                 !EqualityComparer<CredentialEnvelope>.Default.Equals(_credentials, expectedCredentials)))
            {
                return false;
            }
            _queueId = normalizedRegister.QueueId;
            _longPollTimeout = normalizedRegister.EventQueueLongPollTimeout;
            _maxMessageLength = normalizedRegister.MaxMessageLength;
            _maxTopicLength = normalizedRegister.MaxTopicLength;
            _maxFileUploadBytes = checked((long)(normalizedRegister.MaxFileUploadSizeMiB ?? 10) * 1024 * 1024);
            _channelSettingsLimits = new ChannelSettingsLimits(
                normalizedRegister.MaxChannelNameLength,
                normalizedRegister.MaxChannelDescriptionLength,
                normalizedRegister.MaxChannelFolderNameLength,
                normalizedRegister.MaxChannelFolderDescriptionLength);
            _topicVisibilityPolicies = (normalizedRegister.UserTopics ?? [])
                .Where(item => item.ChannelId > 0 && Enum.IsDefined(item.Policy))
                .ToDictionary(item => new ChannelTopic(item.ChannelId, item.Topic).CanonicalKey, item => item.Policy, StringComparer.Ordinal);
            _isOrganizationAdministrator = normalizedRegister.IsOrganizationAdministrator;
            _canCreatePrivateGroup = normalizedRegister.CanCreatePrivateChannel;
            _isPresenceAvailable = normalizedRegister.IsPresenceAvailable;
            _isOwnPresenceEnabled = normalizedRegister.IsOwnPresenceEnabled;
            _ownPresenceStatus = normalizedRegister.IsOwnPresenceEnabled switch
            {
                false => UserPresenceStatus.Offline,
                true => UserPresenceStatus.Active,
                _ => null
            };
            _isUserStatusAvailable = normalizedRegister.IsUserStatusAvailable;
            _isOwnUserStatusConfirmed = normalizedRegister.IsUserStatusAvailable;
            _pendingOwnUserStatusConfirmation = null;
            _pendingOwnUserStatusAfterEventId = 0;
            _lastOwnUserStatusEventId = null;
            _lastOwnUserStatusEventValue = null;
            _recentDirectMessages = MergeRecentDirectMessages(
                normalizedRegister.RecentDirectMessages,
                DeriveRecentDirectMessages(snapshot));
            if (_selectedConversation is { } selected && !IsSupportedConversation(snapshot, selected))
            {
                _selectedConversation = null;
                InvalidateHistoryLocked(clearConversation: true);
            }
            _state = TrimMessageWindow(snapshot, _selectedConversation, retainOldest: false);
        }
        RaiseStateChanged();
        return true;
    }

    private async Task StoreThenApplyAsync(IReadOnlyCollection<DomainEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;
        var accountId = AccountId ?? throw new InvalidOperationException("No account is active.");
        var normalizedEvents = FilterConversationEventsForPersistence(State, NormalizeOwnMessages(events));
        var summariesToRefresh = GetSummaryRefreshConversations(State, normalizedEvents);
        var topicsToRefresh = summariesToRefresh.OfType<ChannelTopic>().ToArray();
        await _store.ApplyBatchAsync(accountId, normalizedEvents, cancellationToken).ConfigureAwait(false);
        var refreshedSummaries = summariesToRefresh.Count == 0
            ? []
            : await _store.QueryConversationSummariesAsync(accountId, summariesToRefresh, cancellationToken).ConfigureAwait(false);
        var refreshedTopics = topicsToRefresh.Length == 0
            ? []
            : await _store.QueryTopicSummariesAsync(accountId, topicsToRefresh, cancellationToken).ConfigureAwait(false);
        Mutate(state =>
        {
            var next = FilterSupportedConversations(DomainReducer.Apply(state, normalizedEvents));
            var summaries = new Dictionary<string, ConversationSummary>(next.ConversationSummaries);
            foreach (var conversation in summariesToRefresh) summaries.Remove(conversation.CanonicalKey);
            foreach (var summary in refreshedSummaries) summaries[summary.Conversation.CanonicalKey] = summary;
            var topics = new Dictionary<string, TopicSummary>(next.Topics, StringComparer.Ordinal);
            foreach (var topic in topicsToRefresh) topics.Remove(topic.CanonicalKey);
            foreach (var topic in refreshedTopics)
            {
                topics[new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey] = topic;
            }
            return FilterSupportedConversations(next with { ConversationSummaries = summaries, Topics = topics });
        });
        foreach (var flags in normalizedEvents.OfType<MessageFlagsChangedEvent>()
                     .Where(static item => string.Equals(item.Flag, "starred", StringComparison.OrdinalIgnoreCase)))
        {
            MessageMutationObserved?.Invoke(this, new MessageMutationObservedEventArgs(
                flags.MessageIds,
                deleted: false,
                isStarred: flags.Operation == MessageFlagOperation.Add));
        }
        foreach (var deleted in normalizedEvents.OfType<MessageDeletedEvent>())
        {
            MessageMutationObserved?.Invoke(this, new MessageMutationObservedEventArgs(
                deleted.MessageIds,
                deleted: true,
                isStarred: null));
        }
        foreach (var upsert in normalizedEvents
                     .OfType<MessageUpsertEvent>()
                     .Where(static item => item.Source == DomainEventSource.Realtime)
                     .DistinctBy(static item => (item.EventId, item.Message.Id)))
        {
            RealtimeMessageReceived?.Invoke(this, new RealtimeMessageReceivedEventArgs(upsert.Message));
        }
        if (normalizedEvents.OfType<OwnPresenceEnabledChangedEvent>().LastOrDefault() is { } presenceSetting)
        {
            ApplyOwnPresenceEnabledChanged(presenceSetting);
        }
        long? currentUserId;
        lock (_stateGate) currentUserId = _credentials?.UserId;
        if (normalizedEvents.OfType<UserStatusChangedEvent>()
                .Where(item => item.UserId == currentUserId && item.EventId is not null)
                .OrderBy(item => item.EventId)
                .LastOrDefault() is { } ownUserStatus)
        {
            lock (_stateGate)
            {
                _lastOwnUserStatusEventId = ownUserStatus.EventId;
                _lastOwnUserStatusEventValue = ownUserStatus.Status;
                if (_pendingOwnUserStatusConfirmation is { } target &&
                    ownUserStatus.EventId > _pendingOwnUserStatusAfterEventId &&
                    UserStatusMatches(ownUserStatus.Status, target))
                {
                    _isOwnUserStatusConfirmed = true;
                    _pendingOwnUserStatusConfirmation = null;
                    _pendingOwnUserStatusAfterEventId = 0;
                }
            }
            RaiseStateChanged();
        }
    }

    private void ApplyOwnPresenceEnabledChanged(OwnPresenceEnabledChangedEvent changed)
    {
        long? currentUserId;
        UserPresenceStatus? status;
        lock (_stateGate)
        {
            if (!_isPresenceAvailable) return;
            _isOwnPresenceEnabled = changed.IsEnabled;
            _ownPresenceStatus = changed.IsEnabled switch
            {
                false => UserPresenceStatus.Offline,
                true when _ownPresenceStatus == UserPresenceStatus.Idle => UserPresenceStatus.Idle,
                true => UserPresenceStatus.Active,
                _ => null
            };
            currentUserId = _credentials?.UserId;
            status = _ownPresenceStatus;
        }

        if (currentUserId is { } userId && status is { } confirmedStatus)
        {
            Mutate(state => SetPresenceValue(state, userId, confirmedStatus, _utcNow()));
        }
        else
        {
            RaiseStateChanged();
        }
    }

    private async Task RunPresenceLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CredentialEnvelope? credentials;
                lock (_stateGate) credentials = _credentials;
                if (credentials is null ||
                    !await TryReportOwnPresenceAsync(credentials, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                await _presenceDelay(PresenceRefreshInterval, cancellationToken).ConfigureAwait(false);
                lock (_stateGate) credentials = _credentials;
                if (credentials is null ||
                    !await TryRefreshRealmPresenceAsync(credentials, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteMessageMutationAsync(
        long messageId,
        MessageMutationKind kind,
        bool requireOwnership,
        Func<CredentialEnvelope, ChatMessage, CancellationToken, Task<DomainEvent>> operation,
        CancellationToken cancellationToken)
    {
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        var lane = _messageMutationLanes.GetOrAdd(messageId, static _ => new SemaphoreSlim(1, 1));
        await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CredentialEnvelope credentials;
            ChatMessage message;
            CancellationToken runToken;
            lock (_stateGate)
            {
                if (_state.Connection.Status != ConnectionStatus.Connected)
                {
                    throw new InvalidOperationException("Message changes require a connected session.");
                }
                credentials = _credentials ?? throw new InvalidOperationException("No credentials are available.");
                message = _state.Messages.GetValueOrDefault(messageId) ??
                    throw new InvalidOperationException("The message is no longer available.");
                if (requireOwnership && message.SenderId != credentials.UserId)
                {
                    throw new InvalidOperationException("Only your own message can be changed.");
                }
                if (_state.MessageMutations.TryGetValue(messageId, out var existing) &&
                    existing.Status is MessageMutationStatus.Submitting or MessageMutationStatus.Uncertain)
                {
                    throw new InvalidOperationException("The previous message change must be reconciled first.");
                }
                runToken = _runCancellation?.Token ?? throw new InvalidOperationException("The session is stopped.");
            }

            SetMessageMutation(new MessageMutationState(messageId, kind, MessageMutationStatus.Submitting));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
            try
            {
                var domainEvent = await operation(credentials, message, linked.Token).ConfigureAwait(false);
                await StoreThenApplyAsync([domainEvent], linked.Token).ConfigureAwait(false);
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception) when (IsMutationResultUncertain(exception))
            {
                SetMessageMutation(new MessageMutationState(
                    messageId,
                    kind,
                    MessageMutationStatus.Uncertain,
                    exception.Code.ToString()));
                if (IsNetwork(exception))
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline, "message_mutation_unknown") });
                }
                throw;
            }
            catch (GatewayException exception)
            {
                SetMessageMutation(new MessageMutationState(
                    messageId,
                    kind,
                    MessageMutationStatus.Failed,
                    exception.Code.ToString()));
                if (IsRateLimited(exception))
                {
                    Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.RateLimited, "message_mutation_rate_limited") });
                }
                throw;
            }
            catch (OperationCanceledException)
            {
                SetMessageMutation(new MessageMutationState(
                    messageId,
                    kind,
                    MessageMutationStatus.Uncertain,
                    GatewayErrorCode.RequestTimedOut.ToString()));
                throw;
            }
            catch
            {
                SetMessageMutation(new MessageMutationState(
                    messageId,
                    kind,
                    MessageMutationStatus.Failed,
                    "local_failure"));
                throw;
            }
        }
        finally
        {
            lane.Release();
        }
    }

    private void SetMessageMutation(MessageMutationState mutation)
    {
        Mutate(state =>
        {
            var mutations = new Dictionary<long, MessageMutationState>(state.MessageMutations)
            {
                [mutation.MessageId] = mutation
            };
            return state with { MessageMutations = mutations };
        });
    }

    private async Task LoadLatestAsync(
        AccountId accountId,
        CredentialEnvelope? credentials,
        ConversationKey conversation,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _store.QueryMessagePageAsync(
                accountId, conversation, null, HistoryPageSize, cancellationToken).ConfigureAwait(false);
            ApplyHistoryPageIfCurrent(conversation, generation, cached.Messages, retainOldest: false);
            SetHistoryStateIfCurrent(conversation, generation, state => state with
            {
                HasOlderInCache = cached.HasOlderInCache
            });

            if (credentials is null)
            {
                SetHistoryStateIfCurrent(conversation, generation, state => state with { IsLoading = false });
                return;
            }

            var history = await _gateway.GetHistoryAsync(
                new HistoryRequest(credentials, conversation, limit: HistoryPageSize), cancellationToken).ConfigureAwait(false);
            var normalizedHistory = NormalizeOwnMessages(history.Messages);
            if (!await StoreHistoryPageIfCurrentAsync(
                    accountId, conversation, generation, normalizedHistory, cancellationToken).ConfigureAwait(false)) return;
            ApplyHistoryPageIfCurrent(conversation, generation, normalizedHistory, retainOldest: false);
            var hasOlderInCache = history.FoundOldest
                ? false
                : await HasOlderInCacheAsync(accountId, conversation, generation, cancellationToken).ConfigureAwait(false);
            SetHistoryStateIfCurrent(conversation, generation, state => state with
            {
                IsLoading = false,
                FoundOldest = history.FoundOldest,
                HasOlderInCache = hasOlderInCache,
                Error = null
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetHistoryStateIfCurrent(conversation, generation, state => state with { IsLoading = false });
            throw;
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsHistoryCurrent(conversation, generation))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
            }
        }
        catch (GatewayException exception) when (IsNetwork(exception))
        {
            if (IsHistoryCurrent(conversation, generation))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline) });
                SetHistoryStateIfCurrent(conversation, generation, state => state with
                {
                    IsLoading = false,
                    Error = "offline"
                });
            }
        }
        catch
        {
            SetHistoryStateIfCurrent(conversation, generation, state => state with
            {
                IsLoading = false,
                Error = "history_failed"
            });
            throw;
        }
    }

    private async Task LoadOlderCoreAsync(
        AccountId accountId,
        CredentialEnvelope? credentials,
        ConversationKey conversation,
        long generation,
        long beforeMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _store.QueryMessagePageAsync(
                accountId, conversation, beforeMessageId, HistoryPageSize, cancellationToken).ConfigureAwait(false);
            ApplyHistoryPageIfCurrent(conversation, generation, cached.Messages, retainOldest: true);
            SetHistoryStateIfCurrent(conversation, generation, state => state with
            {
                HasOlderInCache = cached.HasOlderInCache
            });

            if (credentials is null || !IsHistoryCurrent(conversation, generation))
            {
                SetHistoryStateIfCurrent(conversation, generation, state => state with { IsLoading = false });
                return;
            }

            var history = await _gateway.GetHistoryAsync(
                new HistoryRequest(credentials, conversation, beforeMessageId, includeAnchor: false, limit: HistoryPageSize),
                cancellationToken).ConfigureAwait(false);
            var normalizedHistory = NormalizeOwnMessages(history.Messages);
            if (!await StoreHistoryPageIfCurrentAsync(
                    accountId, conversation, generation, normalizedHistory, cancellationToken).ConfigureAwait(false)) return;
            ApplyHistoryPageIfCurrent(conversation, generation, normalizedHistory, retainOldest: true);
            var hasOlderInCache = history.FoundOldest
                ? false
                : await HasOlderInCacheAsync(accountId, conversation, generation, cancellationToken).ConfigureAwait(false);
            SetHistoryStateIfCurrent(conversation, generation, state => state with
            {
                IsLoading = false,
                FoundOldest = history.FoundOldest,
                HasOlderInCache = hasOlderInCache,
                Error = null
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetHistoryStateIfCurrent(conversation, generation, state => state with { IsLoading = false });
            throw;
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsHistoryCurrent(conversation, generation))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
            }
        }
        catch (GatewayException exception) when (IsNetwork(exception))
        {
            if (IsHistoryCurrent(conversation, generation))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline) });
                SetHistoryStateIfCurrent(conversation, generation, state => state with
                {
                    IsLoading = false,
                    Error = "offline"
                });
            }
        }
        catch
        {
            SetHistoryStateIfCurrent(conversation, generation, state => state with
            {
                IsLoading = false,
                Error = "history_failed"
            });
            throw;
        }
    }

    private async Task LoadMessageAroundAsync(
        AccountId accountId,
        CredentialEnvelope credentials,
        ConversationKey conversation,
        long messageId,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await _gateway.GetMessagesAroundAsync(
                new MessageAroundRequest(credentials, conversation, messageId, BeforeCount: 25, AfterCount: 24),
                cancellationToken).ConfigureAwait(false);
            if (!page.FoundAnchor || !page.Messages.Any(message => message.Id == messageId))
            {
                if (IsHistoryCurrentForAccount(accountId, conversation, generation))
                {
                    SetHistoryStateIfCurrent(conversation, generation, state => state with
                    {
                        IsLoading = false,
                        Error = "message_not_found"
                    });
                }
                return;
            }
            var normalizedPage = NormalizeOwnMessages(page.Messages);
            if (!await StoreHistoryPageIfCurrentAsync(
                    accountId, conversation, generation, normalizedPage, cancellationToken).ConfigureAwait(false)) return;
            ApplyHistoryPageIfCurrent(conversation, generation, normalizedPage, retainOldest: false);
            SetHistoryStateIfCurrent(conversation, generation, state => state with
            {
                IsLoading = false,
                FoundOldest = page.FoundOldest,
                HasOlderInCache = false,
                Error = null
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetHistoryStateIfCurrent(conversation, generation, state => state with { IsLoading = false });
            throw;
        }
        catch (GatewayException exception) when (IsUnauthorized(exception))
        {
            if (IsHistoryCurrentForAccount(accountId, conversation, generation))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (GatewayException exception) when (IsNetwork(exception))
        {
            if (IsHistoryCurrentForAccount(accountId, conversation, generation))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline, "open_message_offline") });
                SetHistoryStateIfCurrent(conversation, generation, state => state with { IsLoading = false, Error = "offline" });
            }
            throw;
        }
        catch (GatewayException exception) when (IsRateLimited(exception))
        {
            if (IsHistoryCurrentForAccount(accountId, conversation, generation))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.RateLimited, "open_message_rate_limited") });
                SetHistoryStateIfCurrent(conversation, generation, state => state with { IsLoading = false, Error = "rate_limited" });
            }
            throw;
        }
    }

    private async Task<bool> HasOlderInCacheAsync(
        AccountId accountId,
        ConversationKey conversation,
        long generation,
        CancellationToken cancellationToken)
    {
        var minimum = MinimumMessageIdIfCurrent(conversation, generation);
        if (minimum is null) return false;
        var page = await _store.QueryMessagePageAsync(
            accountId, conversation, minimum, 1, cancellationToken).ConfigureAwait(false);
        return page.Messages.Count > 0;
    }

    private void ApplyHistoryPageIfCurrent(
        ConversationKey conversation,
        long generation,
        IReadOnlyList<ChatMessage> messages,
        bool retainOldest)
    {
        if (messages.Count == 0) return;
        var changed = false;
        lock (_stateGate)
        {
            if (!IsHistoryCurrentLocked(conversation, generation)) return;
            if (messages.All(message =>
                    _state.Messages.TryGetValue(message.Id, out var existing) &&
                    AreEquivalentHistoryMessages(existing, message)))
            {
                return;
            }
            var next = DomainReducer.Apply(_state, ToHistoryEvents(messages));
            _recentDirectMessages = MergeRecentDirectMessages(_recentDirectMessages, DeriveRecentDirectMessages(next));
            _state = TrimMessageWindow(next, conversation, retainOldest);
            CacheHistoryWindowLocked(conversation, _state.Messages.Values);
            _retainOldestWindow = retainOldest;
            changed = true;
        }
        if (changed) RaiseStateChanged();
    }

    private static bool AreEquivalentHistoryMessages(ChatMessage left, ChatMessage right) =>
        left.Id == right.Id &&
        left.Conversation == right.Conversation &&
        left.SenderId == right.SenderId &&
        string.Equals(left.Content, right.Content, StringComparison.Ordinal) &&
        left.Timestamp == right.Timestamp &&
        left.IsRead == right.IsRead &&
        string.Equals(left.SenderDisplayName, right.SenderDisplayName, StringComparison.Ordinal) &&
        string.Equals(left.SenderAvatarUrl, right.SenderAvatarUrl, StringComparison.Ordinal) &&
        left.IsStarred == right.IsStarred &&
        left.Reactions.SequenceEqual(right.Reactions);

    private void SetHistoryStateIfCurrent(
        ConversationKey conversation,
        long generation,
        Func<ConversationHistoryState, ConversationHistoryState> update)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (!IsHistoryCurrentLocked(conversation, generation)) return;
            var next = update(_historyState) with
            {
                OldestLoadedMessageId = MinimumMessageIdLocked(conversation)
            };
            changed = next != _historyState;
            _historyState = next;
        }
        if (changed) RaiseStateChanged();
    }

    private bool IsHistoryCurrent(ConversationKey conversation, long generation)
    {
        lock (_stateGate) return IsHistoryCurrentLocked(conversation, generation);
    }

    private bool IsHistoryCurrentForAccount(AccountId accountId, ConversationKey conversation, long generation)
    {
        lock (_stateGate)
        {
            return _accountId == accountId && IsHistoryCurrentLocked(conversation, generation);
        }
    }

    private bool IsHistoryCurrentLocked(ConversationKey conversation, long generation) =>
        _selectedConversation == conversation &&
        _historyState.Conversation == conversation &&
        _historyState.Generation == generation;

    private long? MinimumMessageIdIfCurrent(ConversationKey conversation, long generation)
    {
        lock (_stateGate)
        {
            return IsHistoryCurrentLocked(conversation, generation)
                ? MinimumMessageIdLocked(conversation)
                : null;
        }
    }

    private void ApplyLocalMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            RaiseStateChanged();
            return;
        }
        var events = ToHistoryEvents(messages);
        Mutate(state => DomainReducer.Apply(state, events));
    }

    private DomainEvent[] ToHistoryEvents(IEnumerable<ChatMessage> messages) =>
        NormalizeOwnMessages(messages.Select(message => (DomainEvent)new MessageUpsertEvent(message, Source: DomainEventSource.History)));

    private DomainEvent[] NormalizeOwnMessages(IEnumerable<DomainEvent> events)
    {
        long? currentUserId;
        lock (_stateGate) currentUserId = _credentials?.UserId;
        if (currentUserId is null) return events.ToArray();
        return events.Select(domainEvent => domainEvent switch
        {
            MessageUpsertEvent upsert => upsert with { Message = MarkOwnMessageRead(upsert.Message, currentUserId.Value) },
            MessagesUpdatedEvent updated => updated with
            {
                Messages = updated.Messages.Select(message => MarkOwnMessageRead(message, currentUserId.Value)).ToArray()
            },
            SendConfirmedEvent sent => sent with { Message = MarkOwnMessageRead(sent.Message, currentUserId.Value) },
            _ => domainEvent
        }).ToArray();
    }

    private IReadOnlyList<ChatMessage> NormalizeOwnMessages(IEnumerable<ChatMessage> messages)
    {
        long? currentUserId;
        lock (_stateGate) currentUserId = _credentials?.UserId;
        return currentUserId is null
            ? messages.ToArray()
            : messages.Select(message => MarkOwnMessageRead(message, currentUserId.Value)).ToArray();
    }

    private static ChatMessage MarkOwnMessageRead(ChatMessage message, long currentUserId) =>
        message.SenderId == currentUserId && !message.IsRead ? message with { IsRead = true } : message;

    private static IReadOnlyList<DomainEvent> FilterConversationEventsForPersistence(
        ClientState initialState,
        IEnumerable<DomainEvent> events)
    {
        var accepted = new List<DomainEvent>();
        var state = initialState;
        foreach (var domainEvent in events)
        {
            var filtered = domainEvent switch
            {
                MessageUpsertEvent upsert when !IsSupportedConversation(state, upsert.Message.Conversation) => null,
                MessagesUpdatedEvent updated => FilterMessagesUpdatedEvent(state, updated),
                SendConfirmedEvent sent when
                    !IsSupportedConversation(state, sent.Message.Conversation) &&
                    !state.Outbox.ContainsKey(sent.LocalId) => null,
                OutboxQueuedEvent queued when !IsSupportedConversation(state, queued.Entry.Conversation) => null,
                TopicUpsertEvent topic when !IsSupportedConversation(
                    state,
                    new ChannelTopic(topic.Topic.ChannelId, topic.Topic.Topic)) => null,
                _ => domainEvent
            };
            if (filtered is not null)
            {
                accepted.Add(filtered);
            }
            else if (domainEvent.EventId is { } eventId)
            {
                accepted.Add(new IgnoredDomainEvent(
                    domainEvent.GetType().Name,
                    "unsupported_conversation",
                    eventId,
                    domainEvent.Source));
            }
            state = FilterSupportedConversations(DomainReducer.Apply(state, domainEvent));
        }
        return accepted;
    }

    private static DomainEvent? FilterMessagesUpdatedEvent(ClientState state, MessagesUpdatedEvent updated)
    {
        var messages = updated.Messages
            .Where(message => IsSupportedConversation(state, message.Conversation))
            .ToArray();
        return messages.Length == 0 ? null : updated with { Messages = messages };
    }

    private static IReadOnlyCollection<ConversationKey> GetSummaryRefreshConversations(
        ClientState state,
        IEnumerable<DomainEvent> events)
    {
        var conversations = new Dictionary<string, ConversationKey>(StringComparer.Ordinal);
        foreach (var domainEvent in events)
        {
            IEnumerable<long> ids = domainEvent switch
            {
                MessageDeletedEvent deleted => deleted.MessageIds,
                MessageMovedEvent moved => moved.MessageIds,
                MessageContentChangedEvent changed => [changed.MessageId],
                MessageFlagsChangedEvent flags when !flags.AllMessages => flags.MessageIds,
                _ => []
            };
            foreach (var summary in state.ConversationSummaries.Values.Where(summary => ids.Contains(summary.LatestMessage.Id)))
            {
                conversations[summary.Conversation.CanonicalKey] = summary.Conversation;
            }
            if (domainEvent is MessageMovedEvent movedEvent)
            {
                conversations[movedEvent.Destination.CanonicalKey] = movedEvent.Destination;
            }
            else if (domainEvent is MessageFlagsChangedEvent { AllMessages: true })
            {
                foreach (var summary in state.ConversationSummaries.Values)
                {
                    conversations[summary.Conversation.CanonicalKey] = summary.Conversation;
                }
            }
        }
        return conversations.Values.ToArray();
    }


    private static IReadOnlyCollection<DomainEvent> FilterRealtimeEvents(
        IReadOnlyCollection<DomainEvent> events,
        long cursor)
    {
        var acceptedIds = events
            .Where(domainEvent => domainEvent.EventId is { } eventId && eventId > cursor)
            .Select(domainEvent => domainEvent.EventId!.Value)
            .ToHashSet();
        return events
            .Where(domainEvent => domainEvent.EventId is { } eventId && acceptedIds.Contains(eventId))
            .ToArray();
    }

    private long? MinimumMessageIdLocked(ConversationKey conversation)
    {
        return _state.Messages.Values
            .Where(message => message.Conversation == conversation)
            .Select(message => (long?)message.Id)
            .Min();
    }

    private void StartOutboxTimer(string localId, CancellationToken cancellationToken)
    {
        var timer = RunOutboxTimerAsync(localId, cancellationToken);
        _outboxTimers[localId] = timer;
        _ = timer.ContinueWith(
            (completed, state) => ((ConcurrentDictionary<string, Task>)state!).TryRemove(localId, out var ignored),
            _outboxTimers,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunOutboxTimerAsync(string localId, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(OutboxTimingPolicy.WaitDuration, cancellationToken).ConfigureAwait(false);
            if (!MutateOutbox(localId, OutboxState.Hidden, OutboxState.Waiting)) return;
            await _delay(OutboxTimingPolicy.ExpiryDuration - OutboxTimingPolicy.WaitDuration, cancellationToken).ConfigureAwait(false);
            MutateOutbox(localId, OutboxState.Waiting, OutboxState.WaitExpired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool MutateOutbox(string localId, OutboxState expected, OutboxState replacement)
    {
        var changed = false;
        Mutate(state =>
        {
            if (!state.Outbox.TryGetValue(localId, out var entry) || entry.State != expected) return state;
            changed = true;
            var outbox = new Dictionary<string, OutboxEntry>(state.Outbox, StringComparer.Ordinal)
            {
                [localId] = entry with { State = replacement }
            };
            return state with { Outbox = outbox };
        }, publishWhenUnchanged: false);
        return changed;
    }

    private void MarkOutboxFailed(string localId, OutboxFailureKind failure)
    {
        Mutate(state => DomainReducer.Apply(state, new OutboxFailedEvent(localId, failure)));
    }

    private void MarkOutboxWaitExpired(string localId)
    {
        Mutate(state =>
        {
            if (!state.Outbox.TryGetValue(localId, out var entry) || entry.State == OutboxState.Failed) return state;
            var outbox = new Dictionary<string, OutboxEntry>(state.Outbox, StringComparer.Ordinal)
            {
                [localId] = entry with { State = OutboxState.WaitExpired, Failure = null }
            };
            return state with { Outbox = outbox };
        }, publishWhenUnchanged: false);
    }

    private static void ObserveAfterCancellation(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleUnauthorizedAsync()
    {
        AccountId? accountId;
        CancellationTokenSource? runCancellation;
        lock (_stateGate)
        {
            accountId = _accountId;
            runCancellation = _runCancellation;
            _isPresenceAvailable = false;
            _isOwnPresenceEnabled = null;
            _ownPresenceStatus = null;
            _isUserStatusAvailable = false;
            _isOwnUserStatusConfirmed = false;
            _pendingOwnUserStatusConfirmation = null;
            _pendingOwnUserStatusAfterEventId = 0;
            _lastOwnUserStatusEventId = null;
            _lastOwnUserStatusEventValue = null;
            _credentials = null;
            _queueId = null;
        }
        runCancellation?.Cancel();
        var failures = await RemoveCredentialAndLockAsync(accountId).ConfigureAwait(false);
        lock (_stateGate)
        {
            _selectedConversation = null;
            InvalidateHistoryLocked(clearConversation: true);
            _recentDirectMessages = [];
            _state = ClientState.Empty with
            {
                Connection = failures.Count == 0
                    ? new ConnectionState(ConnectionStatus.ReauthRequired)
                    : new ConnectionState(ConnectionStatus.Faulted, "reauth_cleanup_failed")
            };
        }
        RaiseStateChanged();
        if (failures.Count > 0)
        {
            throw new AggregateException("Unauthorized-session cleanup was incomplete.", failures);
        }
    }

    private async Task CleanupFailedLoginAsync(AccountId accountId)
    {
        var failures = await RemoveCredentialAndLockAsync(accountId).ConfigureAwait(false);
        lock (_stateGate)
        {
            _credentials = null;
            _queueId = null;
            _selectedConversation = null;
            InvalidateHistoryLocked(clearConversation: true);
            _recentDirectMessages = [];
            _accountId = accountId;
            _state = ClientState.Empty with
            {
                Connection = failures.Count == 0
                    ? new ConnectionState(ConnectionStatus.Locked)
                    : new ConnectionState(ConnectionStatus.Faulted, "login_cleanup_failed")
            };
        }
        RaiseStateChanged();
        if (failures.Count > 0)
        {
            throw new AggregateException("Login cleanup was incomplete.", failures);
        }
    }

    private async Task RejectLockedRestoreAsync(StoredAccount account)
    {
        Exception? removalFailure = null;
        try
        {
            await _vault.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            removalFailure = exception;
        }
        lock (_stateGate)
        {
            _credentials = null;
            _queueId = null;
            _selectedConversation = null;
            InvalidateHistoryLocked(clearConversation: true);
            _recentDirectMessages = [];
            _accountId = account.AccountId;
            _state = ClientState.Empty with
            {
                Connection = removalFailure is null
                    ? new ConnectionState(ConnectionStatus.Locked, "residual_credential_removed")
                    : new ConnectionState(ConnectionStatus.Faulted, "locked_cache_credential_cleanup_failed")
            };
        }
        RaiseStateChanged();
    }

    private async Task<IReadOnlyList<Exception>> RemoveCredentialAndLockAsync(AccountId? accountId)
    {
        var failures = new List<Exception>();
        try
        {
            await _vault.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (accountId is { } id)
        {
            try
            {
                await _store.SetCacheUnlockedAsync(id, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        return failures;
    }

    private async Task LockAllCachesBestEffortAsync()
    {
        IReadOnlyList<StoredAccount> accounts;
        try
        {
            accounts = await _store.ListAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        foreach (var account in accounts)
        {
            try
            {
                await _store.SetCacheUnlockedAsync(
                    account.AccountId, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Continue locking other independently isolated account caches.
            }
        }
    }

    private async Task StopRunAsync(bool setOffline)
    {
        CancellationTokenSource? cancellation;
        Task? eventLoop;
        Task? presenceLoop;
        lock (_stateGate)
        {
            cancellation = _runCancellation;
            eventLoop = _eventLoop;
            presenceLoop = _presenceLoop;
            _runCancellation = null;
            _eventLoop = null;
            _presenceLoop = null;
            CancelMessageQueriesLocked();
            CancelChannelCatalogLocked();
            CancelChannelSettingsLocked();
            InvalidateHistoryLocked(clearConversation: false);
        }
        cancellation?.Cancel();
        var loops = new[] { eventLoop, presenceLoop }.OfType<Task>().ToArray();
        if (loops.Length > 0)
        {
            try { await Task.WhenAll(loops).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        var timers = _outboxTimers.Values.ToArray();
        if (timers.Length > 0)
        {
            try { await Task.WhenAll(timers).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        if (setOffline && _credentials is not null)
        {
            lock (_stateGate) _ownPresenceStatus = UserPresenceStatus.Offline;
            Mutate(state => state with
            {
                Connection = new ConnectionState(ConnectionStatus.Offline, "stopped"),
                Presence = PresenceState.Unavailable
            });
        }
    }

    private void ResetInMemory(ConnectionState connection, bool clearAccount)
    {
        lock (_stateGate)
        {
            CancelMessageQueriesLocked();
            CancelChannelCatalogLocked();
            CancelChannelSettingsLocked();
            _topicVisibilityPolicies = new Dictionary<string, TopicVisibilityPolicy>(StringComparer.Ordinal);
            _isOrganizationAdministrator = false;
            _canCreatePrivateGroup = false;
            _isPresenceAvailable = false;
            _isOwnPresenceEnabled = null;
            _ownPresenceStatus = null;
            _credentials = null;
            _queueId = null;
            _selectedConversation = null;
            InvalidateHistoryLocked(clearConversation: true);
            _recentDirectMessages = [];
            _historyMemoryCache.Clear();
            _historyMemoryLru.Clear();
            if (clearAccount) _accountId = null;
            _state = ClientState.Empty with { Connection = connection };
        }
        RaiseStateChanged();
    }

    private bool IsMessageQueryCurrent(
        MessageQueryKind kind,
        long generation,
        AccountId accountId,
        long epoch,
        CancellationTokenSource? runCancellation)
    {
        lock (_stateGate)
        {
            return IsMessageQueryCurrentLocked(kind, generation, accountId, epoch, runCancellation);
        }
    }

    private bool IsMessageQueryCurrentLocked(
        MessageQueryKind kind,
        long generation,
        AccountId accountId,
        long epoch,
        CancellationTokenSource? runCancellation) =>
        _accountId == accountId &&
        _queryEpoch == epoch &&
        ReferenceEquals(_runCancellation, runCancellation) &&
        (kind == MessageQueryKind.Search ? _searchQueryGeneration : _savedQueryGeneration) == generation;

    private void CancelMessageQueriesLocked()
    {
        _queryEpoch++;
        _searchQueryGeneration++;
        _savedQueryGeneration++;
        _searchQueryCancellation?.Cancel();
        _savedQueryCancellation?.Cancel();
        _searchQueryCancellation = null;
        _savedQueryCancellation = null;
    }

    private bool IsChannelCatalogCurrent(AccountId accountId, long generation, CancellationTokenSource cancellation) { lock (_stateGate) return IsChannelCatalogCurrentLocked(accountId, generation, cancellation); }
    private bool IsChannelCatalogCurrentLocked(AccountId accountId, long generation, CancellationTokenSource cancellation) => _accountId == accountId && _channelCatalogGeneration == generation && ReferenceEquals(_channelCatalogCancellation, cancellation) && _runCancellation is not null;
    private bool IsChannelOperationCurrent(AccountId accountId, long generation, CancellationTokenSource runCancellation) { lock (_stateGate) return IsChannelOperationCurrentLocked(accountId, generation, runCancellation); }
    private bool IsChannelOperationCurrentLocked(AccountId accountId, long generation, CancellationTokenSource runCancellation) => _accountId == accountId && _queryEpoch == generation && ReferenceEquals(_runCancellation, runCancellation);
    private void CancelChannelCatalogLocked()
    {
        _channelCatalogGeneration++;
        try { _channelCatalogCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _channelCatalogCancellation?.Dispose();
        _channelCatalogCancellation = null;
        _availableChannels = new Dictionary<long, ChannelSummary>();
    }

    private async Task<bool> StoreHistoryPageIfCurrentAsync(
        AccountId accountId,
        ConversationKey conversation,
        long generation,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!IsHistoryCurrentForAccount(accountId, conversation, generation)) return false;
            await _store.StoreMessagePageAsync(accountId, messages, cancellationToken).ConfigureAwait(false);
            return IsHistoryCurrentForAccount(accountId, conversation, generation);
        }
        finally
        {
            _commands.Release();
        }
    }

    private bool IsChannelSettingsCurrent(AccountId accountId, long generation, CancellationTokenSource cancellation) { lock (_stateGate) return IsChannelSettingsCurrentLocked(accountId, generation, cancellation); }
    private bool IsChannelSettingsCurrentLocked(AccountId accountId, long generation, CancellationTokenSource cancellation) => _accountId == accountId && _channelSettingsGeneration == generation && ReferenceEquals(_channelSettingsCancellation, cancellation) && _runCancellation is not null;
    private void CancelChannelSettingsLocked()
    {
        _channelSettingsGeneration++;
        try { _channelSettingsCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _channelSettingsCancellation?.Dispose();
        _channelSettingsCancellation = null;
        _channelSettingsSnapshot = null;
    }

    private IReadOnlyList<TopicSummary> DecorateTopics(IEnumerable<TopicSummary> topics)
    {
        lock (_stateGate)
        {
            return topics.Select(topic => _topicVisibilityPolicies.TryGetValue(new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey, out var policy)
                ? topic with { VisibilityPolicy = policy }
                : topic with { VisibilityPolicy = TopicVisibilityPolicy.None }).ToArray();
        }
    }

    public async Task ClearConversationCacheAsync(
        ConversationKey expectedConversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedConversation);
        ThrowIfDisposed();
        CancellationTokenSource? priorHistoryCancellation;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountId accountId;
            lock (_stateGate)
            {
                if (_selectedConversation is not { } selected || selected != expectedConversation)
                {
                    throw new InvalidOperationException("The selected conversation changed before clearing its cache.");
                }
                accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
                priorHistoryCancellation = _historyCancellation;
                _historyCancellation = null;
                _latestHistoryTask = null;
                _loadOlderTask = null;
                _retainOldestWindow = false;
                var generation = ++_historyGeneration;
                _historyState = new ConversationHistoryState(
                    selected,
                    generation,
                    false,
                    false,
                    false,
                    null,
                    null);
            }

            priorHistoryCancellation?.Cancel();
            priorHistoryCancellation?.Dispose();
            await _store.PurgeConversationAsync(accountId, expectedConversation, cancellationToken).ConfigureAwait(false);

            lock (_stateGate)
            {
                if (_accountId != accountId || _selectedConversation != expectedConversation) return;
                var key = expectedConversation.CanonicalKey;
                _historyMemoryCache.Remove(key);
                var lruNode = _historyMemoryLru.Find(key);
                if (lruNode is not null) _historyMemoryLru.Remove(lruNode);
                _state = _state with
                {
                    Messages = _state.Messages
                        .Where(pair => pair.Value.Conversation != expectedConversation)
                        .ToDictionary(pair => pair.Key, pair => pair.Value),
                    ConversationSummaries = _state.ConversationSummaries
                        .Where(pair => !string.Equals(pair.Key, key, StringComparison.Ordinal))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                };
            }
            RaiseStateChanged();
        }
        finally
        {
            _commands.Release();
        }
    }

    private void ValidateSend(ConversationKey conversation, string content)
    {
        ValidateMessageContent(content);
        EnsureSupportedConversationLocked(conversation);
    }

    private void EnsureSupportedConversationLocked(ConversationKey conversation)
    {
        if (!IsSupportedConversation(_state, conversation))
            throw new InvalidOperationException("This conversation is not supported by RelayCove.");
    }

    private static bool IsSupportedConversation(ClientState state, ConversationKey conversation) => conversation switch
    {
        DirectMessage direct => direct.OtherUserIds.Count <= 1,
        ChannelTopic { Topic.Length: 0 } channel =>
            PrivateGroupPolicy.IsEligible(state.Subscriptions.GetValueOrDefault(channel.ChannelId)),
        _ => false
    };

    private static bool IsSupportedConversationKey(ClientState state, string key)
    {
        if (string.Equals(key, "dm:self", StringComparison.Ordinal)) return true;
        if (key.StartsWith("dm:", StringComparison.Ordinal))
        {
            var participants = key[3..];
            return participants.Length > 0 && !participants.Contains(',') &&
                long.TryParse(participants, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) && userId > 0;
        }

        foreach (var subscription in state.Subscriptions.Values.Where(static item => PrivateGroupPolicy.IsEligible(item)))
        {
            if (string.Equals(key, new ChannelTopic(subscription.ChannelId, string.Empty).CanonicalKey, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static UnreadState FilterSupportedUnread(UnreadState unread, ClientState state)
    {
        var counts = unread.Counts
            .Where(pair => IsSupportedConversationKey(state, pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new UnreadState(counts, reportedTotal: null, unread.IsTruncated);
    }

    private static ClientState FilterSupportedConversations(ClientState state)
    {
        var messages = state.Messages
            .Where(pair => IsSupportedConversation(state, pair.Value.Conversation))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var topics = state.Topics
            .Where(pair => pair.Value.Topic.Length == 0 &&
                PrivateGroupPolicy.IsEligible(state.Subscriptions.GetValueOrDefault(pair.Value.ChannelId)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var summaries = state.ConversationSummaries
            .Where(pair => IsSupportedConversation(state, pair.Value.Conversation))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var outbox = state.Outbox
            .Where(pair => IsSupportedConversation(state, pair.Value.Conversation))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return state with
        {
            Messages = messages,
            Topics = topics,
            ConversationSummaries = summaries,
            Outbox = outbox,
            Unread = FilterSupportedUnread(state.Unread, state)
        };
    }

    private void ValidateMessageContent(string content)
    {
        if (content.Length > _maxMessageLength)
        {
            throw new ArgumentException("Message exceeds the server limit.", nameof(content));
        }
    }

    private void ValidateAttachmentUpload(AttachmentUpload upload)
    {
        var fileName = upload.FileName.Trim();
        if (fileName.Length == 0 || fileName.Length > 256 || fileName.Any(character => character < 0x20 || character == 0x7f))
        {
            throw new ArgumentException("Attachment file name is invalid.", nameof(upload));
        }
        if (upload.Length <= 0 || upload.Length > MaxFileUploadBytes)
        {
            throw new ArgumentException("Attachment size is outside the server limit.", nameof(upload));
        }
        if (upload.Content.CanSeek && upload.Content.Length - upload.Content.Position < upload.Length)
        {
            throw new ArgumentException("Attachment stream is shorter than the declared length.", nameof(upload));
        }
    }

    private CredentialEnvelope GetConnectedCredentials()
    {
        lock (_stateGate)
        {
            if (_state.Connection.Status != ConnectionStatus.Connected) throw new InvalidOperationException("The session is not connected.");
            return _credentials ?? throw new InvalidOperationException("No credentials are available.");
        }
    }

    private void Mutate(Func<ClientState, ClientState> update, bool publishWhenUnchanged = true)
    {
        bool changed;
        lock (_stateGate)
        {
            var next = FilterSupportedConversations(update(_state));
            changed = !ReferenceEquals(next, _state);
            _recentDirectMessages = MergeRecentDirectMessages(
                _recentDirectMessages,
                DeriveRecentDirectMessages(next));
            if (_selectedConversation is { } selected && !IsSupportedConversation(next, selected))
            {
                _selectedConversation = null;
                InvalidateHistoryLocked(clearConversation: true);
            }
            _state = TrimMessageWindow(next, _selectedConversation, _retainOldestWindow);
            if (_selectedConversation is { } current)
            {
                CacheHistoryWindowLocked(current, _state.Messages.Values);
            }
        }
        if (changed || publishWhenUnchanged) RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        ClientState snapshot;
        lock (_stateGate) snapshot = _state;
        StateChanged?.Invoke(this, new ClientStateChangedEventArgs(snapshot));
    }

    private void InvalidateHistoryLocked(bool clearConversation)
    {
        var prior = _historyState;
        var conversation = clearConversation ? null : _selectedConversation;
        var generation = ++_historyGeneration;
        _historyCancellation?.Cancel();
        _historyCancellation?.Dispose();
        _historyCancellation = null;
        _latestHistoryTask = null;
        _loadOlderTask = null;
        _retainOldestWindow = false;
        _historyState = new ConversationHistoryState(
            conversation,
            generation,
            false,
            conversation is not null && prior.Conversation == conversation && prior.FoundOldest,
            conversation is not null && prior.Conversation == conversation && prior.HasOlderInCache,
            conversation is not null && prior.Conversation == conversation ? prior.OldestLoadedMessageId : null,
            null);
    }

    private static ClientState TrimMessageWindow(
        ClientState state,
        ConversationKey? conversation,
        bool retainOldest)
    {
        if (conversation is null)
        {
            return state.Messages.Count == 0
                ? state
                : state with { Messages = new Dictionary<long, ChatMessage>() };
        }

        var selected = state.Messages.Values
            .Where(message => message.Conversation == conversation)
            .OrderBy(message => message.Id)
            .ToArray();
        if (selected.Length > MessageWindowLimit)
        {
            selected = retainOldest
                ? selected.Take(MessageWindowLimit).ToArray()
                : selected.TakeLast(MessageWindowLimit).ToArray();
        }
        if (selected.Length == state.Messages.Count &&
            selected.All(message => state.Messages.ContainsKey(message.Id)))
        {
            return state;
        }
        return state with { Messages = selected.ToDictionary(message => message.Id) };
    }

    private void CacheSelectedHistoryLocked()
    {
        if (_selectedConversation is not { } selected) return;
        CacheHistoryWindowLocked(selected, _state.Messages.Values);
    }

    private void SeedHistoryMemoryCacheLocked(IEnumerable<ChatMessage> messages)
    {
        _historyMemoryCache.Clear();
        _historyMemoryLru.Clear();
        foreach (var group in messages.GroupBy(message => message.Conversation.CanonicalKey, StringComparer.Ordinal))
        {
            CacheHistoryWindowLocked(group.First().Conversation, group);
        }
    }

    private void CacheHistoryWindowLocked(ConversationKey conversation, IEnumerable<ChatMessage> messages)
    {
        var window = messages
            .Where(message => message.Conversation == conversation)
            .OrderBy(message => message.Id)
            .TakeLast(MessageWindowLimit)
            .ToArray();
        if (window.Length == 0) return;

        var key = conversation.CanonicalKey;
        _historyMemoryCache[key] = window;
        TouchHistoryMemoryWindowLocked(key);
        while (_historyMemoryCache.Count > HistoryMemoryCacheLimit && _historyMemoryLru.First is { } oldest)
        {
            _historyMemoryLru.RemoveFirst();
            _historyMemoryCache.Remove(oldest.Value);
        }
    }

    private bool TryGetHistoryMemoryWindowLocked(ConversationKey conversation, out ChatMessage[] messages)
    {
        var key = conversation.CanonicalKey;
        if (!_historyMemoryCache.TryGetValue(key, out var cached))
        {
            messages = [];
            return false;
        }
        messages = cached;
        TouchHistoryMemoryWindowLocked(key);
        return true;
    }

    private void TouchHistoryMemoryWindowLocked(string key)
    {
        var existing = _historyMemoryLru.Find(key);
        if (existing is not null) _historyMemoryLru.Remove(existing);
        _historyMemoryLru.AddLast(key);
    }

    private static StoredAccount ToStoredAccount(CredentialEnvelope credentials)
    {
        var accountId = RelayCove.Core.AccountId.Create(credentials.Realm, credentials.UserId);
        return new StoredAccount(accountId, credentials.Realm, credentials.Email, credentials.UserId);
    }

    private static IReadOnlyList<ConversationKey> DeriveRecentDirectMessages(ClientState state) => state.Messages.Values
        .Where(message => message.Conversation is DirectMessage { OtherUserIds.Count: <= 1 })
        .GroupBy(message => message.Conversation.CanonicalKey, StringComparer.Ordinal)
        .OrderByDescending(group => group.Max(message => message.Timestamp))
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => group.First().Conversation)
        .ToArray();

    private static IReadOnlyList<ConversationKey> MergeRecentDirectMessages(
        IEnumerable<ConversationKey> primary,
        IEnumerable<ConversationKey> additional) => primary
        .Concat(additional)
        .OfType<DirectMessage>()
        .Where(static conversation => conversation.OtherUserIds.Count <= 1)
        .DistinctBy(conversation => conversation.CanonicalKey)
        .Cast<ConversationKey>()
        .ToArray();

    private static OutboxFailureKind MapSendFailure(GatewayException exception) => exception.Kind switch
    {
        GatewayErrorKind.ReauthRequired or GatewayErrorKind.AuthenticationFailed => OutboxFailureKind.ReauthenticationRequired,
        GatewayErrorKind.RateLimited => OutboxFailureKind.RateLimited,
        GatewayErrorKind.Offline => OutboxFailureKind.NetworkResultUnknown,
        GatewayErrorKind.Protocol => OutboxFailureKind.Protocol,
        _ => OutboxFailureKind.Rejected
    };

    private static bool IsUnauthorized(GatewayException exception) =>
        exception.StatusCode == 401 ||
        exception.Code == GatewayErrorCode.Unauthorized ||
        exception.Kind == GatewayErrorKind.ReauthRequired;

    private static bool IsQueueExpired(GatewayException exception) =>
        exception.Code == GatewayErrorCode.BadEventQueueId || exception.Kind == GatewayErrorKind.QueueExpired;

    private static bool IsRateLimited(GatewayException exception) =>
        exception.StatusCode == 429 || exception.Code == GatewayErrorCode.RateLimited || exception.Kind == GatewayErrorKind.RateLimited;

    private static bool IsNetwork(GatewayException exception) =>
        exception.Kind == GatewayErrorKind.Offline ||
        exception.Code is GatewayErrorCode.NetworkError or GatewayErrorCode.RequestTimedOut;

    private static bool IsMutationResultUncertain(GatewayException exception) =>
        IsNetwork(exception) ||
        exception.Kind is GatewayErrorKind.Server or GatewayErrorKind.Protocol;

    private enum MessageQueryKind
    {
        Search,
        Saved
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
