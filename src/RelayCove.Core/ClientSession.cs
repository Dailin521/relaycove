using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelayCove.Core;

public sealed class ClientSession : IClientSession, IMessageMutationObserver, IAsyncDisposable
{
    private static long s_nextLocalId;
    private static readonly TimeSpan ServerRestartRecoveryWindow = TimeSpan.FromMinutes(5);
    private const int HistoryPageSize = 50;
    private const int MessageWindowLimit = 250;
    private const int HistoryMemoryCacheLimit = 12;

    private readonly IZulipGateway _gateway;
    private readonly IAccountStore _store;
    private readonly ICredentialVault _vault;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan, CancellationToken, Task> _sendDeadlineDelay;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan> _serverRestartDelay;
    private readonly SemaphoreSlim _commands = new(1, 1);
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
    private int _disposed;

    public ClientSession(
        IZulipGateway gateway,
        IAccountStore store,
        ICredentialVault vault,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan>? serverRestartDelay = null,
        Func<TimeSpan, CancellationToken, Task>? sendDeadlineDelay = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _delay = delay ?? Task.Delay;
        _sendDeadlineDelay = sendDeadlineDelay ?? Task.Delay;
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
                _state = (cached?.State ?? ClientState.Empty) with
                {
                    Connection = new ConnectionState(ConnectionStatus.Offline, "cache_first")
                };
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return LoadMessageQueryAsync(
            MessageQueryKind.Search,
            (credentials, token) => _gateway.SearchMessagesAsync(
                new MessageSearchRequest(credentials, query.Trim(), beforeMessageId, limit), token),
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
            if (credentials is null) return cached;
            try
            {
                var result = await _gateway.GetTopicsAsync(new TopicsRequest(credentials, channelId), cancellationToken).ConfigureAwait(false);
                var events = result.Topics.Select(topic => (DomainEvent)new TopicUpsertEvent(topic, Source: DomainEventSource.History)).ToArray();
                await StoreThenApplyAsync(events, cancellationToken).ConfigureAwait(false);
                return result.Topics;
            }
            catch (GatewayException exception) when (IsUnauthorized(exception))
            {
                await HandleUnauthorizedAsync().ConfigureAwait(false);
                throw;
            }
            catch (GatewayException exception) when (IsNetwork(exception))
            {
                Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline) });
                return cached;
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
            return result;
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

    public async Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default)
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

    public async Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credentials = GetConnectedCredentials();
            var conversation = SelectedConversation ?? throw new InvalidOperationException("No conversation is selected.");
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

    private async Task ApplyRegisterAsync(RegisterResult register, CancellationToken cancellationToken)
    {
        var accountId = AccountId ?? throw new InvalidOperationException("No account is active.");
        var normalizedRegister = register with { Events = NormalizeOwnMessages(register.Events) };
        await _store.ReplaceRegisterSnapshotAsync(accountId, normalizedRegister, cancellationToken).ConfigureAwait(false);
        var loaded = await _store.LoadAsync(accountId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, OutboxEntry> outbox;
        lock (_stateGate) outbox = _state.Outbox;
        var snapshot = loaded?.State ?? new ClientState(
            subscriptions: normalizedRegister.Subscriptions.ToDictionary(item => item.ChannelId),
            users: normalizedRegister.Users.ToDictionary(item => item.UserId),
            unread: normalizedRegister.Unread);
        snapshot = DomainReducer.Apply(snapshot, normalizedRegister.Events) with
        {
            Outbox = new Dictionary<string, OutboxEntry>(outbox, StringComparer.Ordinal),
            Connection = new ConnectionState(ConnectionStatus.Connected),
            LastEventId = register.LastEventId
        };
        lock (_stateGate)
        {
            _queueId = normalizedRegister.QueueId;
            _longPollTimeout = normalizedRegister.EventQueueLongPollTimeout;
            _maxMessageLength = normalizedRegister.MaxMessageLength;
            _maxTopicLength = normalizedRegister.MaxTopicLength;
            _maxFileUploadBytes = checked((long)(normalizedRegister.MaxFileUploadSizeMiB ?? 10) * 1024 * 1024);
            _recentDirectMessages = MergeRecentDirectMessages(
                normalizedRegister.RecentDirectMessages,
                DeriveRecentDirectMessages(snapshot));
            if (_selectedConversation is ChannelTopic selected &&
                (!snapshot.Subscriptions.TryGetValue(selected.ChannelId, out var selectedSubscription) || !selectedSubscription.IsActive))
            {
                _selectedConversation = null;
                InvalidateHistoryLocked(clearConversation: true);
            }
            _state = TrimMessageWindow(snapshot, _selectedConversation, retainOldest: false);
        }
        RaiseStateChanged();
    }

    private async Task StoreThenApplyAsync(IReadOnlyCollection<DomainEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;
        var accountId = AccountId ?? throw new InvalidOperationException("No account is active.");
        var normalizedEvents = NormalizeOwnMessages(events);
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
            var next = DomainReducer.Apply(state, normalizedEvents);
            var summaries = new Dictionary<string, ConversationSummary>(next.ConversationSummaries);
            foreach (var conversation in summariesToRefresh) summaries.Remove(conversation.CanonicalKey);
            foreach (var summary in refreshedSummaries) summaries[summary.Conversation.CanonicalKey] = summary;
            var topics = new Dictionary<string, TopicSummary>(next.Topics, StringComparer.Ordinal);
            foreach (var topic in topicsToRefresh) topics.Remove(topic.CanonicalKey);
            foreach (var topic in refreshedTopics)
            {
                topics[new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey] = topic;
            }
            return next with { ConversationSummaries = summaries, Topics = topics };
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
            await _store.StoreMessagePageAsync(accountId, normalizedHistory, cancellationToken).ConfigureAwait(false);
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
            if (normalizedHistory.Count > 0)
            {
                try
                {
                    await MarkDisplayedReadForGenerationAsync(accountId, credentials, conversation, generation, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (GatewayException exception) when (IsUnauthorized(exception))
                {
                    if (IsHistoryCurrent(conversation, generation))
                    {
                        await HandleUnauthorizedAsync().ConfigureAwait(false);
                    }
                }
                catch (GatewayException)
                {
                    // The latest page is already authoritative and visible. A separate
                    // mark-read failure must leave it loaded and unread for a later
                    // explicit activation instead of turning history into a failure.
                }
                catch
                {
                    if (IsHistoryCurrentForAccount(accountId, conversation, generation))
                    {
                        Mutate(state => state with
                        {
                            Connection = new ConnectionState(ConnectionStatus.Faulted, "mark_read_cache_failed")
                        });
                    }
                }
            }
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
            await _store.StoreMessagePageAsync(accountId, normalizedHistory, cancellationToken).ConfigureAwait(false);
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
            if (!IsHistoryCurrentForAccount(accountId, conversation, generation)) return;
            var normalizedPage = NormalizeOwnMessages(page.Messages);
            await _store.StoreMessagePageAsync(accountId, normalizedPage, cancellationToken).ConfigureAwait(false);
            if (!IsHistoryCurrentForAccount(accountId, conversation, generation)) return;
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

    private async Task MarkDisplayedReadForGenerationAsync(
        AccountId accountId,
        CredentialEnvelope credentials,
        ConversationKey conversation,
        long generation,
        CancellationToken cancellationToken)
    {
        ChatMessage[] unread;
        lock (_stateGate)
        {
            if (_accountId != accountId || !IsHistoryCurrentLocked(conversation, generation)) return;
            unread = _state.Messages.Values
                .Where(message => message.Conversation == conversation && !message.IsRead)
                .OrderByDescending(message => message.Id)
                .Take(HistoryPageSize)
                .ToArray();
        }
        if (unread.Length == 0) return;
        await _gateway.MarkReadAsync(
            new MarkReadRequest(credentials, conversation, unread.Max(message => message.Id), unread.Length),
            cancellationToken).ConfigureAwait(false);
        if (!IsHistoryCurrentForAccount(accountId, conversation, generation)) return;
        var flags = new MessageFlagsChangedEvent(
            unread.Select(message => message.Id).ToArray(), false, MessageFlagOperation.Add, "read", Source: DomainEventSource.Local);
        await StoreThenApplyAsync([flags], cancellationToken).ConfigureAwait(false);
    }

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
        Task? loop;
        lock (_stateGate)
        {
            cancellation = _runCancellation;
            loop = _eventLoop;
            _runCancellation = null;
            _eventLoop = null;
            CancelMessageQueriesLocked();
            CancelChannelCatalogLocked();
            InvalidateHistoryLocked(clearConversation: false);
        }
        cancellation?.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        var timers = _outboxTimers.Values.ToArray();
        if (timers.Length > 0)
        {
            try { await Task.WhenAll(timers).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        if (setOffline && _credentials is not null)
        {
            Mutate(state => state with { Connection = new ConnectionState(ConnectionStatus.Offline, "stopped") });
        }
    }

    private void ResetInMemory(ConnectionState connection, bool clearAccount)
    {
        lock (_stateGate)
        {
            CancelMessageQueriesLocked();
            CancelChannelCatalogLocked();
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

    private void ValidateSend(ConversationKey conversation, string content)
    {
        ValidateMessageContent(content);
        if (conversation is ChannelTopic channel)
        {
            if (!_state.Subscriptions.TryGetValue(channel.ChannelId, out var subscription) || !subscription.IsActive)
            {
                throw new InvalidOperationException("The channel is not subscribed.");
            }
            if (channel.Topic.Length > _maxTopicLength) throw new ArgumentException("Topic exceeds the server limit.", nameof(conversation));
        }
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
            var next = update(_state);
            changed = !ReferenceEquals(next, _state);
            _recentDirectMessages = MergeRecentDirectMessages(
                _recentDirectMessages,
                DeriveRecentDirectMessages(next));
            if (_selectedConversation is ChannelTopic selected &&
                (!next.Subscriptions.TryGetValue(selected.ChannelId, out var selectedSubscription) || !selectedSubscription.IsActive))
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
        .Where(message => message.Conversation is DirectMessage)
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
