using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;
using RelayCove.Client.Auth;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountShellCoordinator : IAsyncDisposable
{
    private readonly object stateGate = new();
    // These primitives intentionally remain undisposed after cancellation because queued
    // operations can still observe the lifetime token while shutdown is converging.
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly IClientPersistentAuthentication authentication;
    private readonly IClientAccountRuntimeFactory runtimeFactory;
    private readonly ClientNotificationActivationRouter activationRouter;
    private readonly Func<string> deviceNameProvider;
    private readonly Func<string> clientVersionProvider;
    private readonly ILogger<ClientAccountShellCoordinator> logger;
    private ClientAccountShellSnapshot snapshot = ClientAccountShellSnapshot.Initial;
    private IClientAccountRuntime? runtime;
    private RuntimeSubscription? runtimeSubscription;
    private IDisposable? activationLease;
    private ClientActivitySnapshot latestActivity = ClientActivitySnapshot.Inactive;
    private string? activeDisplayName;
    private Uri? activeServerBaseUri;
    private LocalConversationListReadOutcome conversationList =
        LocalConversationListReadOutcome.Failure(
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            revision: 0);
    private ClientMessageListSnapshot messageList = ClientMessageListSnapshot.Initial;
    private MessageSelection? messageSelection;
    private Guid? renderedConversationId;
    private long conversationPublicationRevision;
    private long messagePublicationRevision;
    private long shellPublicationRevision;
    private Task? disposeTask;
    private bool detachedForProcessExit;
    private int disposeStarted;

    public ClientAccountShellCoordinator(
        IClientPersistentAuthentication authentication,
        IClientAccountRuntimeFactory runtimeFactory,
        ClientNotificationActivationRouter activationRouter,
        ILogger<ClientAccountShellCoordinator> logger,
        Func<string>? deviceNameProvider = null,
        Func<string>? clientVersionProvider = null)
    {
        this.authentication = authentication ??
            throw new ArgumentNullException(nameof(authentication));
        this.runtimeFactory = runtimeFactory ??
            throw new ArgumentNullException(nameof(runtimeFactory));
        this.activationRouter = activationRouter ??
            throw new ArgumentNullException(nameof(activationRouter));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.deviceNameProvider = deviceNameProvider ?? GetDeviceName;
        this.clientVersionProvider = clientVersionProvider ?? GetClientVersion;
    }

    public event Action<ClientAccountShellSnapshot>? SnapshotChanged;

    public event Action<LocalConversationListReadOutcome>? ConversationListChanged;

    public event Action<ClientMessageListSnapshot>? MessageListChanged;

    public ClientAccountShellSnapshot Snapshot => Volatile.Read(ref snapshot);

    public LocalConversationListReadOutcome ConversationList =>
        Volatile.Read(ref conversationList);

    public ClientMessageListSnapshot MessageList => Volatile.Read(ref messageList);

    public Task RestoreAsync(CancellationToken cancellationToken = default) =>
        StartRestoreAsync(cancellationToken);

    public Task LoginAsync(
        string serverAddress,
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        if (!TryCreateLoginRequest(
                serverAddress,
                userName,
                password,
                out var serverBaseUri,
                out var request))
        {
            return PublishValidationFailureAsync(cancellationToken);
        }

        return AuthenticateAsync(
            ClientAccountShellPhase.SigningIn,
            token => authentication.LoginAsync(serverBaseUri!, request!, token),
            cancellationToken);
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            var activeRuntime = runtime;
            if (activeRuntime is null)
            {
                return;
            }

            PublishActiveSnapshot(
                ClientAccountShellPhase.Retrying,
                activeRuntime.ConnectionState,
                Snapshot.LastSyncStatus);
            var outcome = await activeRuntime
                .RetryRealtimeAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            if (outcome.Status == ClientSyncRunStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(activeRuntime)
                    .ConfigureAwait(false);
                return;
            }

            if (outcome.Status == ClientSyncRunStatus.Completed)
            {
                EnsureActivationLease(activeRuntime);
            }

            PublishActiveSnapshot(
                ClientAccountShellPhase.Active,
                activeRuntime.ConnectionState,
                outcome.Status);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            PublishStoppingSnapshot();
        }
        catch (OperationCanceledException)
        {
            RestoreCurrentActiveSnapshot();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Retrying the active account failed; errorType={ErrorType}.",
                exception.GetType().Name);
            RestoreCurrentActiveSnapshot(ClientSyncRunStatus.RemoteFailure);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            var activeRuntime = runtime;
            if (activeRuntime is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut());
                return;
            }

            PublishActiveSnapshot(
                ClientAccountShellPhase.SigningOut,
                activeRuntime.ConnectionState,
                Snapshot.LastSyncStatus);
            ClearActiveOwnership(out var lease, out var detachedRuntime);
            lease?.Dispose();

            if (detachedRuntime is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut());
                return;
            }

            var logoutStatus = await CompleteRuntimeLogoutAsync(detachedRuntime)
                .ConfigureAwait(false);

            PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                logoutStatus: logoutStatus));
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            PublishStoppingSnapshot();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void UpdateActivity(ClientActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        IClientAccountRuntime? activeRuntime;
        lock (stateGate)
        {
            latestActivity = activity.OpenConversationId is null
                ? activity
                : activity with { OpenConversationId = null };
            activeRuntime = runtime;
            activity = BuildRuntimeActivityLocked();
        }

        if (activeRuntime is null || Volatile.Read(ref disposeStarted) != 0)
        {
            return;
        }

        try
        {
            activeRuntime.UpdateActivity(activity);
        }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref disposeStarted) != 0 ||
            !IsCurrentRuntime(activeRuntime))
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Publishing account window activity failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }

        TryCommitPendingRenderedBoundary();
    }

    public void SelectConversation(
        Guid? conversationId,
        long? targetMessageId = null)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A selected conversation ID cannot be empty.",
                nameof(conversationId));
        }

        if (targetMessageId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMessageId));
        }

        ThrowIfStopping();
        MessageSelection? previousSelection;
        MessageSelection? nextSelection = null;
        IClientAccountRuntime? activeRuntime;
        ClientActivitySnapshot activity;
        lock (stateGate)
        {
            activeRuntime = runtime;
            var isAuthorizedSelection = conversationId is { } selectedId &&
                activeRuntime is not null &&
                runtimeSubscription is not null &&
                conversationList.Status == LocalCacheOperationStatus.Ready &&
                conversationList.Conversations.Any(item => item.Id == selectedId);
            if (isAuthorizedSelection &&
                messageSelection is { } currentSelection &&
                currentSelection.ConversationId == conversationId &&
                (targetMessageId is null ||
                 currentSelection.TargetMessageId == targetMessageId))
            {
                return;
            }

            previousSelection = messageSelection;
            messageSelection = null;
            renderedConversationId = null;
            if (isAuthorizedSelection)
            {
                nextSelection = new MessageSelection(
                    conversationId!.Value,
                    targetMessageId,
                    runtimeSubscription!,
                    CancellationTokenSource.CreateLinkedTokenSource(
                        lifetimeCancellation.Token));
                messageSelection = nextSelection;
            }

            activity = BuildRuntimeActivityLocked();
        }

        previousSelection?.Cancel();
        PublishMessageList(nextSelection is null
            ? ClientMessageListSnapshot.Initial
            : CreateMessageSnapshot(
                nextSelection,
                ClientMessageListStatus.Loading,
                isLoading: true,
                lastLoadStatus: null));
        TryUpdateRuntimeActivity(activeRuntime, activity);
        if (nextSelection is not null)
        {
            _ = OpenMessageSelectionAsync(nextSelection);
        }
    }

    public Task LoadOlderMessagesAsync()
    {
        ThrowIfStopping();
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !IsCurrentMessageSelectionLocked(selection) ||
                !messageList.CanLoadOlder ||
                Interlocked.CompareExchange(ref selection.OlderLoadRunning, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }
        }

        return LoadOlderMessagesCoreAsync(selection);
    }

    public void AcknowledgeMessageSnapshotApplied(
        Guid conversationId,
        long revision,
        long? observedThroughMessageId,
        bool isAtLatestRegion)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An applied conversation ID cannot be empty.",
                nameof(conversationId));
        }

        if (observedThroughMessageId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedThroughMessageId));
        }

        IClientAccountRuntime? activeRuntime;
        ClientActivitySnapshot activity;
        lock (stateGate)
        {
            if (messageSelection is not { } selection ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                messageList.ConversationId != conversationId ||
                messageList.Revision != revision ||
                (observedThroughMessageId.HasValue &&
                 !selection.Messages.ContainsKey(observedThroughMessageId.Value)))
            {
                return;
            }

            selection.AppliedRevision = revision;
            if (observedThroughMessageId is { } observedMessageId)
            {
                selection.PendingObservedThroughMessageId = Math.Max(
                    selection.PendingObservedThroughMessageId ?? 0,
                    observedMessageId);
            }

            renderedConversationId = isAtLatestRegion ? conversationId : null;
            activeRuntime = runtime;
            activity = BuildRuntimeActivityLocked();
        }

        TryUpdateRuntimeActivity(activeRuntime, activity);
        TryCommitPendingRenderedBoundary();
    }

    public void DetachForProcessExit()
    {
        IDisposable? lease;
        RuntimeSubscription? subscription;
        MessageSelection? selection;
        lock (stateGate)
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            {
                return;
            }

            detachedForProcessExit = true;
            lease = activationLease;
            activationLease = null;
            subscription = runtimeSubscription;
            runtimeSubscription = null;
            selection = messageSelection;
            messageSelection = null;
            renderedConversationId = null;
            SnapshotChanged = null;
            ConversationListChanged = null;
            MessageListChanged = null;
        }

        lifetimeCancellation.Cancel();
        selection?.Cancel();
        subscription?.Detach();
        lease?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Task sharedDispose;
        TaskCompletionSource? completion = null;
        lock (stateGate)
        {
            if (disposeTask is null)
            {
                if (detachedForProcessExit)
                {
                    disposeTask = Task.CompletedTask;
                }
                else
                {
                    Interlocked.Exchange(ref disposeStarted, 1);
                    completion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    disposeTask = completion.Task;
                }
            }

            sharedDispose = disposeTask;
        }

        if (completion is not null)
        {
            lifetimeCancellation.Cancel();
            _ = CompleteDisposeAsync(completion);
        }

        return new ValueTask(sharedDispose);
    }

    private Task StartRestoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        return AuthenticateAsync(
            ClientAccountShellPhase.Restoring,
            token => authentication.RestoreAsync(token),
            cancellationToken);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ClearActiveOwnership(out var lease, out var activeRuntime);
                lease?.Dispose();
                if (activeRuntime is not null)
                {
                    try
                    {
                        await activeRuntime.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            "Disposing the active account during application shutdown failed; " +
                            "errorType={ErrorType}.",
                            exception.GetType().Name);
                    }
                }

                PublishStoppingSnapshot();
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (stateGate)
            {
                SnapshotChanged = null;
                ConversationListChanged = null;
                MessageListChanged = null;
            }

            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        }
    }

    private async Task PublishValidationFailureAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            if (runtime is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                    PersistentClientAuthenticationStatus.ValidationFailed));
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task AuthenticateAsync(
        ClientAccountShellPhase phase,
        Func<CancellationToken, Task<PersistentClientAuthenticationOutcome>> authenticate,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        ClientAuthenticationSession? unownedSession = null;
        try
        {
            ThrowIfStopping();
            if (runtime is not null)
            {
                return;
            }

            PublishSnapshot(new ClientAccountShellSnapshot(
                phase,
                AuthenticationStatus: null,
                DisplayName: null,
                ServerBaseUri: null,
                ConnectionState.Disconnected,
                LastSyncStatus: null,
                LastLogoutStatus: null,
                RetryAfter: null));

            var outcome = await authenticate(linkedCancellation.Token).ConfigureAwait(false);
            unownedSession = outcome.Session;
            if (outcome.Status != PersistentClientAuthenticationStatus.Authenticated ||
                unownedSession is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                    outcome.Status,
                    retryAfter: outcome.RetryAfter));
                return;
            }

            linkedCancellation.Token.ThrowIfCancellationRequested();
            var displayName = unownedSession.DisplayName;
            var serverBaseUri = unownedSession.ServerBaseUri;
            PublishSnapshot(new ClientAccountShellSnapshot(
                ClientAccountShellPhase.Starting,
                outcome.Status,
                displayName,
                serverBaseUri,
                ConnectionState.Connecting,
                LastSyncStatus: null,
                LastLogoutStatus: null,
                RetryAfter: null));

            IClientAccountRuntime? unownedRuntime = await runtimeFactory
                .CreateAsync(unownedSession, linkedCancellation.Token)
                .ConfigureAwait(false);
            unownedSession = null;
            IDisposable? unownedLease = null;
            try
            {
                ClientActivitySnapshot activityBeforeStart;
                lock (stateGate)
                {
                    activityBeforeStart = latestActivity;
                }

                unownedRuntime.UpdateActivity(activityBeforeStart);
                var startOutcome = await unownedRuntime
                    .StartAsync(linkedCancellation.Token)
                    .ConfigureAwait(false);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                if (startOutcome.StartupSyncOutcome.Status ==
                    ClientSyncRunStatus.AuthenticationRequired)
                {
                    var logoutStatus = await CompleteRuntimeLogoutAsync(unownedRuntime)
                        .ConfigureAwait(false);
                    unownedRuntime = null;
                    PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                        PersistentClientAuthenticationStatus.AuthenticationFailed,
                        logoutStatus));
                    return;
                }

                if (startOutcome.IsAuthoritativeCacheReady)
                {
                    unownedLease = activationRouter.ActivateAccount(
                        unownedRuntime.Identity.Id,
                        unownedRuntime.TryAuthorizeNotificationTarget);
                }

                var subscription = new RuntimeSubscription(this, unownedRuntime);
                subscription.Attach();
                lock (stateGate)
                {
                    ThrowIfStopping();
                    runtime = unownedRuntime;
                    runtimeSubscription = subscription;
                    activationLease = unownedLease;
                    activeDisplayName = displayName;
                    activeServerBaseUri = serverBaseUri;
                }

                unownedRuntime = null;
                unownedLease = null;
                RestoreLatestActivity();
                PublishActiveSnapshot(
                    ClientAccountShellPhase.Active,
                    startOutcome.RealtimeState,
                    startOutcome.StartupSyncOutcome.Status);
                RequestConversationRefresh(subscription);
            }
            finally
            {
                unownedLease?.Dispose();
                if (unownedRuntime is not null)
                {
                    await unownedRuntime.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            PublishStoppingSnapshot();
        }
        catch (OperationCanceledException)
        {
            PublishSnapshot(ClientAccountShellSnapshot.SignedOut());
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposeStarted) != 0)
        {
            PublishStoppingSnapshot();
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Starting a production account failed; errorType={ErrorType}.",
                exception.GetType().Name);
            PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                PersistentClientAuthenticationStatus.RemoteFailure));
        }
        finally
        {
            if (unownedSession is not null)
            {
                await unownedSession.DisposeAsync().ConfigureAwait(false);
            }

            operationGate.Release();
        }
    }

    private bool TryCreateLoginRequest(
        string serverAddress,
        string userName,
        string password,
        out Uri? serverBaseUri,
        out LoginRequest? request)
    {
        serverBaseUri = null;
        request = null;
        var trimmedUserName = userName?.Trim();
        if (string.IsNullOrWhiteSpace(serverAddress) ||
            string.IsNullOrWhiteSpace(trimmedUserName) ||
            trimmedUserName.Length > 64 ||
            string.IsNullOrEmpty(password) ||
            password.Length > 1_024 ||
            !Uri.TryCreate(serverAddress.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        try
        {
            serverBaseUri = ClientAuthenticationUri.CanonicalizeServerBaseUri(parsed);
            var deviceName = deviceNameProvider();
            var clientVersion = clientVersionProvider();
            if (string.IsNullOrWhiteSpace(deviceName) ||
                deviceName.Length > 128 ||
                string.IsNullOrWhiteSpace(clientVersion) ||
                clientVersion.Length > 64)
            {
                return false;
            }

            request = new LoginRequest(
                trimmedUserName,
                password,
                deviceName,
                clientVersion);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(
                "Validating a login request failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }

    private void PublishActiveSnapshot(
        ClientAccountShellPhase phase,
        RelayCove.Shared.Realtime.ConnectionState connectionState,
        ClientSyncRunStatus? syncStatus)
    {
        ClientAccountShellSnapshot value;
        Action<ClientAccountShellSnapshot>? handlers;
        lock (stateGate)
        {
            value = new ClientAccountShellSnapshot(
                phase,
                PersistentClientAuthenticationStatus.Authenticated,
                activeDisplayName,
                activeServerBaseUri,
                connectionState,
                syncStatus,
                LastLogoutStatus: null,
                RetryAfter: null,
                TotalUnreadCount: conversationList.Status == LocalCacheOperationStatus.Ready
                    ? conversationList.TotalUnreadCount
                    : 0,
                Revision: Interlocked.Increment(ref shellPublicationRevision));
            Volatile.Write(ref snapshot, value);
            handlers = SnapshotChanged;
        }

        PublishSnapshotHandlers(value, handlers);
    }

    private void RestoreCurrentActiveSnapshot(ClientSyncRunStatus? syncStatus = null)
    {
        IClientAccountRuntime? activeRuntime;
        lock (stateGate)
        {
            activeRuntime = runtime;
        }

        if (activeRuntime is not null)
        {
            PublishActiveSnapshot(
                ClientAccountShellPhase.Active,
                activeRuntime.ConnectionState,
                syncStatus ?? Snapshot.LastSyncStatus);
        }
    }

    private async Task EndAuthenticationRequiredSessionAsync(
        IClientAccountRuntime expectedRuntime)
    {
        IDisposable? lease;
        IClientAccountRuntime? detachedRuntime;
        RuntimeSubscription? subscription;
        MessageSelection? selection;
        lock (stateGate)
        {
            if (!ReferenceEquals(runtime, expectedRuntime))
            {
                return;
            }

            lease = activationLease;
            detachedRuntime = runtime;
            subscription = runtimeSubscription;
            activationLease = null;
            runtime = null;
            runtimeSubscription = null;
            selection = messageSelection;
            messageSelection = null;
            renderedConversationId = null;
            activeDisplayName = null;
            activeServerBaseUri = null;
        }

        subscription?.Detach();
        selection?.Cancel();
        PublishConversationList(LocalConversationListReadOutcome.Failure(
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            ConversationList.Revision));
        PublishMessageList(ClientMessageListSnapshot.Initial);
        lease?.Dispose();
        var logoutStatus = detachedRuntime is null
            ? ClientLogoutStatus.LoggedOut
            : await CompleteRuntimeLogoutAsync(detachedRuntime).ConfigureAwait(false);
        PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
            PersistentClientAuthenticationStatus.AuthenticationFailed,
            logoutStatus));
    }

    private async Task<ClientLogoutStatus> CompleteRuntimeLogoutAsync(
        IClientAccountRuntime accountRuntime)
    {
        ClientLogoutStatus logoutStatus;
        try
        {
            logoutStatus = await accountRuntime
                .LogoutAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Logging out an account runtime did not fully converge; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            logoutStatus = ClientLogoutStatus.RemoteFailure;
        }

        try
        {
            await accountRuntime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Disposing a logged-out account runtime did not fully converge; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }

        return logoutStatus;
    }

    private bool IsCurrentRuntime(IClientAccountRuntime expectedRuntime)
    {
        lock (stateGate)
        {
            return ReferenceEquals(runtime, expectedRuntime);
        }
    }

    private bool IsCurrentSubscription(RuntimeSubscription expectedSubscription)
    {
        lock (stateGate)
        {
            return ReferenceEquals(runtimeSubscription, expectedSubscription) &&
                ReferenceEquals(runtime, expectedSubscription.Runtime) &&
                Volatile.Read(ref disposeStarted) == 0;
        }
    }

    private void OnRuntimeConnectionStateChanged(
        RuntimeSubscription subscription,
        RelayCove.Shared.Realtime.ConnectionState connectionState)
    {
        TryPublishRuntimeActiveSnapshot(subscription, connectionState);
    }

    private void OnRuntimeConversationStateChanged(
        RuntimeSubscription subscription,
        long revision)
    {
        _ = revision;
        RequestConversationRefresh(subscription);
        RequestMessageRefresh(subscription);
    }

    private void RequestConversationRefresh(RuntimeSubscription subscription)
    {
        if (!IsCurrentSubscription(subscription))
        {
            return;
        }

        Volatile.Write(ref subscription.RefreshPending, 1);
        if (Interlocked.CompareExchange(ref subscription.RefreshRunning, 1, 0) == 0)
        {
            _ = RefreshConversationListAsync(subscription);
        }
    }

    private async Task RefreshConversationListAsync(RuntimeSubscription subscription)
    {
        try
        {
            while (Interlocked.Exchange(ref subscription.RefreshPending, 0) == 1 &&
                IsCurrentSubscription(subscription))
            {
                LocalConversationListReadOutcome outcome;
                try
                {
                    outcome = await subscription.Runtime
                        .ReadConversationListAsync(lifetimeCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    lifetimeCancellation.IsCancellationRequested ||
                    !IsCurrentSubscription(subscription))
                {
                    return;
                }
                catch (ObjectDisposedException) when (!IsCurrentSubscription(subscription))
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Refreshing the active conversation list failed; " +
                        "errorType={ErrorType}.",
                        exception.GetType().Name);
                    outcome = LocalConversationListReadOutcome.Failure(
                        LocalCacheOperationStatus.TransientFailure,
                        ConversationList.Revision);
                }

                if (!IsCurrentSubscription(subscription))
                {
                    return;
                }

                if (!TryPublishConversationList(subscription, outcome))
                {
                    return;
                }

                ReconcileMessageSelection(subscription, outcome);

                TryPublishRuntimeActiveSnapshot(
                    subscription,
                    subscription.Runtime.ConnectionState);
            }
        }
        finally
        {
            Volatile.Write(ref subscription.RefreshRunning, 0);
            if (Volatile.Read(ref subscription.RefreshPending) != 0 &&
                IsCurrentSubscription(subscription))
            {
                RequestConversationRefresh(subscription);
            }
        }
    }

    private async Task OpenMessageSelectionAsync(MessageSelection selection)
    {
        try
        {
            var local = await selection.Subscription.Runtime
                .ReadMessagePageAsync(
                    selection.ConversationId,
                    beforeMessageId: null,
                    limit: 50,
                    selection.Token)
                .ConfigureAwait(false);
            if (!TryApplyLocalPage(selection, local, replacePagingState: true))
            {
                return;
            }

            if (local.Status != LocalCacheOperationStatus.Ready)
            {
                TryPublishMessageSelection(
                    selection,
                    MapLocalMessageStatus(local.Status),
                    isLoading: false,
                    lastLoadStatus: null);
                return;
            }

            TryPublishMessageSelection(
                selection,
                ClientMessageListStatus.Ready,
                isLoading: true,
                lastLoadStatus: null);

            if (selection.TargetMessageId is { } targetMessageId &&
                !SelectionContainsMessage(selection, targetMessageId))
            {
                var around = await selection.Subscription.Runtime
                    .LoadMessageAroundAsync(
                        selection.ConversationId,
                        targetMessageId,
                        before: 20,
                        after: 20,
                        selection.Token)
                    .ConfigureAwait(false);
                if (around.Status == ClientMessageLoadStatus.AuthenticationRequired)
                {
                    await EndAuthenticationRequiredSessionAsync(
                            selection.Subscription.Runtime)
                        .ConfigureAwait(false);
                    return;
                }

                if (!TryApplyAroundOutcome(selection, around))
                {
                    return;
                }

                TryPublishMessageSelection(
                    selection,
                    around.Status == ClientMessageLoadStatus.Completed
                        ? ClientMessageListStatus.Ready
                        : MapMessageLoadStatus(around.Status),
                    isLoading: false,
                    lastLoadStatus: around.Status == ClientMessageLoadStatus.Completed
                        ? null
                        : around.Status);
                return;
            }

            var history = await selection.Subscription.Runtime
                .LoadMessageHistoryAsync(
                    selection.ConversationId,
                    beforeMessageId: null,
                    limit: 50,
                    selection.Token)
                .ConfigureAwait(false);
            if (history.Status == ClientMessageLoadStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(selection.Subscription.Runtime)
                    .ConfigureAwait(false);
                return;
            }

            if (!TryApplyHistoryOutcome(selection, history))
            {
                return;
            }

            TryPublishMessageSelection(
                selection,
                history.Status is ClientMessageLoadStatus.AccessRevoked or
                    ClientMessageLoadStatus.AuthenticationRequired or
                    ClientMessageLoadStatus.LocalCacheFailure or
                    ClientMessageLoadStatus.ProtocolError
                    ? MapMessageLoadStatus(history.Status)
                    : ClientMessageListStatus.Ready,
                isLoading: false,
                lastLoadStatus: history.Status == ClientMessageLoadStatus.Completed
                    ? null
                    : history.Status);
        }
        catch (OperationCanceledException) when (
            selection.Token.IsCancellationRequested ||
            lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (!IsCurrentMessageSelection(selection))
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Opening the selected message list failed; errorType={ErrorType}.",
                exception.GetType().Name);
            TryPublishMessageSelection(
                selection,
                ClientMessageListStatus.LocalCacheFailure,
                isLoading: false,
                lastLoadStatus: ClientMessageLoadStatus.LocalCacheFailure);
        }
    }

    private async Task LoadOlderMessagesCoreAsync(MessageSelection selection)
    {
        try
        {
            long? beforeMessageId;
            lock (stateGate)
            {
                if (!IsCurrentMessageSelectionLocked(selection) ||
                    selection.Messages.Count == 0)
                {
                    return;
                }

                beforeMessageId = selection.NextBeforeMessageId ??
                    selection.Messages.Keys.First();
            }

            TryPublishMessageSelection(
                selection,
                ClientMessageListStatus.Ready,
                isLoading: true,
                lastLoadStatus: null);
            var local = await selection.Subscription.Runtime
                .ReadMessagePageAsync(
                    selection.ConversationId,
                    beforeMessageId,
                    limit: 50,
                    selection.Token)
                .ConfigureAwait(false);
            if (!TryApplyLocalPage(selection, local, replacePagingState: false))
            {
                return;
            }

            if (local.Status != LocalCacheOperationStatus.Ready)
            {
                TryPublishMessageSelection(
                    selection,
                    MapLocalMessageStatus(local.Status),
                    isLoading: false,
                    lastLoadStatus: null);
                return;
            }

            TryPublishMessageSelection(
                selection,
                ClientMessageListStatus.Ready,
                isLoading: true,
                lastLoadStatus: null);
            var history = await selection.Subscription.Runtime
                .LoadMessageHistoryAsync(
                    selection.ConversationId,
                    beforeMessageId,
                    limit: 50,
                    selection.Token)
                .ConfigureAwait(false);
            if (history.Status == ClientMessageLoadStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(selection.Subscription.Runtime)
                    .ConfigureAwait(false);
                return;
            }

            if (!TryApplyHistoryOutcome(selection, history))
            {
                return;
            }

            TryPublishMessageSelection(
                selection,
                history.Status is ClientMessageLoadStatus.AccessRevoked or
                    ClientMessageLoadStatus.AuthenticationRequired or
                    ClientMessageLoadStatus.LocalCacheFailure or
                    ClientMessageLoadStatus.ProtocolError
                    ? MapMessageLoadStatus(history.Status)
                    : ClientMessageListStatus.Ready,
                isLoading: false,
                lastLoadStatus: history.Status == ClientMessageLoadStatus.Completed
                    ? null
                    : history.Status);
        }
        catch (OperationCanceledException) when (
            selection.Token.IsCancellationRequested ||
            lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (!IsCurrentMessageSelection(selection))
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Loading older messages failed; errorType={ErrorType}.",
                exception.GetType().Name);
            TryPublishMessageSelection(
                selection,
                ClientMessageListStatus.Ready,
                isLoading: false,
                lastLoadStatus: ClientMessageLoadStatus.LocalCacheFailure);
        }
        finally
        {
            Volatile.Write(ref selection.OlderLoadRunning, 0);
        }
    }

    private bool TryApplyLocalPage(
        MessageSelection selection,
        LocalMessagePageReadOutcome outcome,
        bool replacePagingState)
    {
        lock (stateGate)
        {
            if (!IsCurrentMessageSelectionLocked(selection) ||
                outcome.ConversationId != selection.ConversationId)
            {
                return false;
            }

            if (outcome.Status == LocalCacheOperationStatus.Ready)
            {
                MergeMessagesLocked(selection, outcome.Messages);
                if (replacePagingState || outcome.HasMoreBefore)
                {
                    selection.HasMoreBefore = outcome.HasMoreBefore;
                    selection.NextBeforeMessageId = outcome.NextBeforeMessageId;
                }
            }

            return true;
        }
    }

    private bool TryApplyHistoryOutcome(
        MessageSelection selection,
        ClientMessageHistoryPageOutcome outcome)
    {
        lock (stateGate)
        {
            if (!IsCurrentMessageSelectionLocked(selection))
            {
                return false;
            }

            if (outcome.Status == ClientMessageLoadStatus.Completed)
            {
                MergeMessagesLocked(selection, outcome.Messages);
                selection.HasMoreBefore = outcome.HasMore;
                selection.NextBeforeMessageId = outcome.NextBeforeMessageId;
            }

            return true;
        }
    }

    private bool TryApplyAroundOutcome(
        MessageSelection selection,
        ClientMessageAroundOutcome outcome)
    {
        lock (stateGate)
        {
            if (!IsCurrentMessageSelectionLocked(selection))
            {
                return false;
            }

            if (outcome.Status == ClientMessageLoadStatus.Completed &&
                outcome.TargetMessageId == selection.TargetMessageId)
            {
                MergeMessagesLocked(selection, outcome.Messages);
                selection.HasMoreBefore = outcome.HasMoreBefore;
                selection.NextBeforeMessageId =
                    outcome.HasMoreBefore && outcome.Messages.Count != 0
                        ? outcome.Messages[0].Id
                        : null;
                selection.HasMoreAfter = outcome.HasMoreAfter;
            }

            return true;
        }
    }

    private static void MergeMessagesLocked(
        MessageSelection selection,
        IEnumerable<MessageDto> messages)
    {
        foreach (var message in messages)
        {
            if (message.ConversationId == selection.ConversationId)
            {
                selection.Messages[message.Id] = message;
            }
        }
    }

    private bool SelectionContainsMessage(MessageSelection selection, long messageId)
    {
        lock (stateGate)
        {
            return IsCurrentMessageSelectionLocked(selection) &&
                selection.Messages.ContainsKey(messageId);
        }
    }

    private void ReconcileMessageSelection(
        RuntimeSubscription subscription,
        LocalConversationListReadOutcome outcome)
    {
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !ReferenceEquals(selection.Subscription, subscription))
            {
                return;
            }

            if (outcome.Status == LocalCacheOperationStatus.Ready &&
                outcome.Conversations.Any(item => item.Id == selection.ConversationId))
            {
                return;
            }
        }

        SelectConversation(conversationId: null);
    }

    private void RequestMessageRefresh(RuntimeSubscription subscription)
    {
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !ReferenceEquals(selection.Subscription, subscription) ||
                !IsCurrentMessageSelectionLocked(selection))
            {
                return;
            }
        }

        Volatile.Write(ref selection.RefreshPending, 1);
        if (Interlocked.CompareExchange(ref selection.RefreshRunning, 1, 0) == 0)
        {
            _ = RefreshSelectedMessagesAsync(selection);
        }
    }

    private async Task RefreshSelectedMessagesAsync(MessageSelection selection)
    {
        try
        {
            while (Interlocked.Exchange(ref selection.RefreshPending, 0) == 1 &&
                IsCurrentMessageSelection(selection))
            {
                var local = await selection.Subscription.Runtime
                    .ReadMessagePageAsync(
                        selection.ConversationId,
                        beforeMessageId: null,
                        limit: 50,
                        selection.Token)
                    .ConfigureAwait(false);
                if (!TryApplyLocalPage(selection, local, replacePagingState: false))
                {
                    return;
                }

                TryPublishMessageSelection(
                    selection,
                    local.Status == LocalCacheOperationStatus.Ready
                        ? ClientMessageListStatus.Ready
                        : MapLocalMessageStatus(local.Status),
                    isLoading: Volatile.Read(ref selection.OlderLoadRunning) != 0,
                    lastLoadStatus: null);
            }
        }
        catch (OperationCanceledException) when (selection.Token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (!IsCurrentMessageSelection(selection))
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Refreshing selected messages failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref selection.RefreshRunning, 0);
            if (Volatile.Read(ref selection.RefreshPending) != 0 &&
                IsCurrentMessageSelection(selection))
            {
                RequestMessageRefresh(selection.Subscription);
            }
        }
    }

    private void TryCommitPendingRenderedBoundary()
    {
        MessageSelection? selection;
        IClientAccountRuntime? activeRuntime;
        long messageId;
        lock (stateGate)
        {
            selection = messageSelection;
            activeRuntime = runtime;
            if (selection is null ||
                activeRuntime is null ||
                !IsCurrentMessageSelectionLocked(selection) ||
                !latestActivity.IsMainWindowForeground ||
                selection.PendingObservedThroughMessageId is not { } pendingMessageId ||
                pendingMessageId <= selection.CommittedObservedThroughMessageId ||
                Interlocked.CompareExchange(ref selection.RenderCommitRunning, 1, 0) != 0)
            {
                return;
            }

            messageId = pendingMessageId;
        }

        _ = CompleteRenderedBoundaryAsync(selection, activeRuntime, messageId);
    }

    private async Task CompleteRenderedBoundaryAsync(
        MessageSelection selection,
        IClientAccountRuntime expectedRuntime,
        long messageId)
    {
        var committed = false;
        try
        {
            var status = await expectedRuntime
                .MarkConversationRenderedThroughAsync(
                    selection.ConversationId,
                    messageId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            lock (stateGate)
            {
                if (status == LocalCacheOperationStatus.Ready)
                {
                    committed = true;
                    selection.CommittedObservedThroughMessageId = Math.Max(
                        selection.CommittedObservedThroughMessageId,
                        messageId);
                    if (selection.PendingObservedThroughMessageId <= messageId)
                    {
                        selection.PendingObservedThroughMessageId = null;
                    }
                }
            }
        }
        catch (ObjectDisposedException) when (!IsCurrentRuntime(expectedRuntime))
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Committing a rendered message boundary failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref selection.RenderCommitRunning, 0);
            if (committed)
            {
                TryCommitPendingRenderedBoundary();
            }
        }
    }

    private bool TryPublishMessageSelection(
        MessageSelection selection,
        ClientMessageListStatus status,
        bool isLoading,
        ClientMessageLoadStatus? lastLoadStatus)
    {
        ClientMessageListSnapshot value;
        Action<ClientMessageListSnapshot>? handlers;
        lock (stateGate)
        {
            if (!IsCurrentMessageSelectionLocked(selection))
            {
                return false;
            }

            value = CreateMessageSnapshot(
                selection,
                status,
                isLoading,
                lastLoadStatus) with
            {
                Revision = Interlocked.Increment(ref messagePublicationRevision),
            };
            Volatile.Write(ref messageList, value);
            handlers = MessageListChanged;
        }

        PublishMessageListHandlers(value, handlers);
        return true;
    }

    private void PublishMessageList(ClientMessageListSnapshot value)
    {
        Action<ClientMessageListSnapshot>? handlers;
        lock (stateGate)
        {
            value = value with
            {
                Revision = Interlocked.Increment(ref messagePublicationRevision),
            };
            Volatile.Write(ref messageList, value);
            handlers = MessageListChanged;
        }

        PublishMessageListHandlers(value, handlers);
    }

    private void PublishMessageListHandlers(
        ClientMessageListSnapshot value,
        Action<ClientMessageListSnapshot>? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<ClientMessageListSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing an account message-list snapshot failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private static ClientMessageListSnapshot CreateMessageSnapshot(
        MessageSelection selection,
        ClientMessageListStatus status,
        bool isLoading,
        ClientMessageLoadStatus? lastLoadStatus) =>
        new(
            status,
            selection.ConversationId,
            ClientMessageListPresenter.Present(
                selection.Messages.Values,
                selection.Subscription.Runtime.Identity.UserId),
            isLoading,
            selection.HasMoreBefore,
            selection.HasMoreAfter,
            selection.TargetMessageId,
            lastLoadStatus);

    private static ClientMessageListStatus MapLocalMessageStatus(
        LocalCacheOperationStatus status) =>
        status switch
        {
            LocalCacheOperationStatus.Ready => ClientMessageListStatus.Ready,
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired =>
                ClientMessageListStatus.AuthoritativeSnapshotRequired,
            LocalCacheOperationStatus.RevokedConversation or
                LocalCacheOperationStatus.UnknownConversation =>
                ClientMessageListStatus.RevokedConversation,
            LocalCacheOperationStatus.TransientFailure =>
                ClientMessageListStatus.TransientFailure,
            LocalCacheOperationStatus.FatalScope => ClientMessageListStatus.FatalScope,
            LocalCacheOperationStatus.ProtocolError or LocalCacheOperationStatus.Conflict =>
                ClientMessageListStatus.ProtocolError,
            _ => ClientMessageListStatus.LocalCacheFailure,
        };

    private static ClientMessageListStatus MapMessageLoadStatus(
        ClientMessageLoadStatus status) =>
        status switch
        {
            ClientMessageLoadStatus.Completed => ClientMessageListStatus.Ready,
            ClientMessageLoadStatus.AuthenticationRequired =>
                ClientMessageListStatus.AuthenticationRequired,
            ClientMessageLoadStatus.AccessRevoked =>
                ClientMessageListStatus.RevokedConversation,
            ClientMessageLoadStatus.AccessDenied => ClientMessageListStatus.AccessDenied,
            ClientMessageLoadStatus.TransientFailure =>
                ClientMessageListStatus.TransientFailure,
            ClientMessageLoadStatus.ProtocolError => ClientMessageListStatus.ProtocolError,
            ClientMessageLoadStatus.RemoteFailure => ClientMessageListStatus.RemoteFailure,
            ClientMessageLoadStatus.LocalCacheFailure =>
                ClientMessageListStatus.LocalCacheFailure,
            ClientMessageLoadStatus.Canceled => ClientMessageListStatus.None,
            _ => ClientMessageListStatus.LocalCacheFailure,
        };

    private bool IsCurrentMessageSelection(MessageSelection selection)
    {
        lock (stateGate)
        {
            return IsCurrentMessageSelectionLocked(selection);
        }
    }

    private bool IsCurrentMessageSelectionLocked(MessageSelection selection) =>
        ReferenceEquals(messageSelection, selection) &&
        ReferenceEquals(runtimeSubscription, selection.Subscription) &&
        ReferenceEquals(runtime, selection.Subscription.Runtime) &&
        Volatile.Read(ref disposeStarted) == 0;

    private void PublishConversationList(LocalConversationListReadOutcome value)
    {
        Action<LocalConversationListReadOutcome>? handlers;
        lock (stateGate)
        {
            value = value with
            {
                Revision = Interlocked.Increment(ref conversationPublicationRevision),
            };
            Volatile.Write(ref conversationList, value);
            handlers = ConversationListChanged;
        }

        PublishConversationListHandlers(value, handlers);
    }

    private bool TryPublishConversationList(
        RuntimeSubscription subscription,
        LocalConversationListReadOutcome value)
    {
        Action<LocalConversationListReadOutcome>? handlers;
        lock (stateGate)
        {
            if (!ReferenceEquals(runtimeSubscription, subscription) ||
                !ReferenceEquals(runtime, subscription.Runtime) ||
                Volatile.Read(ref disposeStarted) != 0)
            {
                return false;
            }

            value = value with
            {
                Revision = Interlocked.Increment(ref conversationPublicationRevision),
            };
            Volatile.Write(ref conversationList, value);
            handlers = ConversationListChanged;
        }

        PublishConversationListHandlers(value, handlers);
        return true;
    }

    private void PublishConversationListHandlers(
        LocalConversationListReadOutcome value,
        Action<LocalConversationListReadOutcome>? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<LocalConversationListReadOutcome> handler in
            handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing an account conversation-list snapshot failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private void RestoreLatestActivity()
    {
        IClientAccountRuntime? activeRuntime;
        ClientActivitySnapshot activity;
        lock (stateGate)
        {
            activeRuntime = runtime;
            activity = BuildRuntimeActivityLocked();
        }

        if (activeRuntime is null)
        {
            return;
        }

        try
        {
            activeRuntime.UpdateActivity(activity);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Restoring account window activity failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private ClientActivitySnapshot BuildRuntimeActivityLocked() =>
        renderedConversationId is null
            ? latestActivity
            : latestActivity with { OpenConversationId = renderedConversationId };

    private void TryUpdateRuntimeActivity(
        IClientAccountRuntime? expectedRuntime,
        ClientActivitySnapshot activity)
    {
        if (expectedRuntime is null ||
            Volatile.Read(ref disposeStarted) != 0 ||
            !IsCurrentRuntime(expectedRuntime))
        {
            return;
        }

        try
        {
            expectedRuntime.UpdateActivity(activity);
        }
        catch (ObjectDisposedException) when (!IsCurrentRuntime(expectedRuntime))
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Publishing selected-conversation activity failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void EnsureActivationLease(IClientAccountRuntime expectedRuntime)
    {
        lock (stateGate)
        {
            if (!ReferenceEquals(runtime, expectedRuntime) || activationLease is not null)
            {
                return;
            }
        }

        IDisposable? unownedLease = activationRouter.ActivateAccount(
            expectedRuntime.Identity.Id,
            expectedRuntime.TryAuthorizeNotificationTarget);
        try
        {
            lock (stateGate)
            {
                ThrowIfStopping();
                if (ReferenceEquals(runtime, expectedRuntime) && activationLease is null)
                {
                    activationLease = unownedLease;
                    unownedLease = null;
                }
            }
        }
        finally
        {
            unownedLease?.Dispose();
        }
    }

    private void ClearActiveOwnership(
        out IDisposable? lease,
        out IClientAccountRuntime? activeRuntime)
    {
        RuntimeSubscription? subscription;
        MessageSelection? selection;
        lock (stateGate)
        {
            lease = activationLease;
            activeRuntime = runtime;
            subscription = runtimeSubscription;
            activationLease = null;
            runtime = null;
            runtimeSubscription = null;
            selection = messageSelection;
            messageSelection = null;
            renderedConversationId = null;
            activeDisplayName = null;
            activeServerBaseUri = null;
        }

        subscription?.Detach();
        selection?.Cancel();
        PublishConversationList(LocalConversationListReadOutcome.Failure(
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            ConversationList.Revision));
        PublishMessageList(ClientMessageListSnapshot.Initial);
    }

    private void PublishStoppingSnapshot()
    {
        PublishSnapshot(new ClientAccountShellSnapshot(
            ClientAccountShellPhase.Stopping,
            AuthenticationStatus: null,
            DisplayName: null,
            ServerBaseUri: null,
            RelayCove.Shared.Realtime.ConnectionState.Disconnected,
            LastSyncStatus: null,
            LastLogoutStatus: null,
            RetryAfter: null));
    }

    private void PublishSnapshot(ClientAccountShellSnapshot value)
    {
        Action<ClientAccountShellSnapshot>? handlers;
        lock (stateGate)
        {
            value = value with
            {
                Revision = Interlocked.Increment(ref shellPublicationRevision),
            };
            Volatile.Write(ref snapshot, value);
            handlers = SnapshotChanged;
        }

        PublishSnapshotHandlers(value, handlers);
    }

    private bool TryPublishRuntimeActiveSnapshot(
        RuntimeSubscription subscription,
        RelayCove.Shared.Realtime.ConnectionState connectionState)
    {
        ClientAccountShellSnapshot value;
        Action<ClientAccountShellSnapshot>? handlers;
        lock (stateGate)
        {
            var currentSnapshot = snapshot;
            if (!ReferenceEquals(runtimeSubscription, subscription) ||
                !ReferenceEquals(runtime, subscription.Runtime) ||
                Volatile.Read(ref disposeStarted) != 0 ||
                currentSnapshot.Phase is not ClientAccountShellPhase.Active and
                    not ClientAccountShellPhase.Retrying)
            {
                return false;
            }

            value = currentSnapshot with
            {
                ConnectionState = connectionState,
                TotalUnreadCount = conversationList.Status == LocalCacheOperationStatus.Ready
                    ? conversationList.TotalUnreadCount
                    : 0,
                Revision = Interlocked.Increment(ref shellPublicationRevision),
            };
            Volatile.Write(ref snapshot, value);
            handlers = SnapshotChanged;
        }

        PublishSnapshotHandlers(value, handlers);
        return true;
    }

    private void PublishSnapshotHandlers(
        ClientAccountShellSnapshot value,
        Action<ClientAccountShellSnapshot>? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<ClientAccountShellSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing an account shell snapshot failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private sealed class MessageSelection
    {
        private readonly CancellationTokenSource cancellation;
        private int canceled;

        public MessageSelection(
            Guid conversationId,
            long? targetMessageId,
            RuntimeSubscription subscription,
            CancellationTokenSource cancellation)
        {
            ConversationId = conversationId;
            TargetMessageId = targetMessageId;
            Subscription = subscription;
            this.cancellation = cancellation;
            Token = cancellation.Token;
        }

        public Guid ConversationId { get; }

        public long? TargetMessageId { get; }

        public RuntimeSubscription Subscription { get; }

        public CancellationToken Token { get; }

        public SortedDictionary<long, MessageDto> Messages { get; } = [];

        public long? NextBeforeMessageId { get; set; }

        public bool HasMoreBefore { get; set; }

        public bool HasMoreAfter { get; set; }

        public long AppliedRevision { get; set; }

        public long? PendingObservedThroughMessageId { get; set; }

        public long CommittedObservedThroughMessageId { get; set; }

        public int OlderLoadRunning;

        public int RefreshPending;

        public int RefreshRunning;

        public int RenderCommitRunning;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref canceled, 1) != 0)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed class RuntimeSubscription
    {
        private readonly ClientAccountShellCoordinator owner;
        private int attached;

        public RuntimeSubscription(
            ClientAccountShellCoordinator owner,
            IClientAccountRuntime runtime)
        {
            this.owner = owner;
            Runtime = runtime;
        }

        public IClientAccountRuntime Runtime { get; }

        public int RefreshPending;

        public int RefreshRunning;

        public void Attach()
        {
            if (Interlocked.Exchange(ref attached, 1) != 0)
            {
                return;
            }

            Runtime.ConnectionStateChanged += OnConnectionStateChanged;
            Runtime.ConversationStateChanged += OnConversationStateChanged;
        }

        public void Detach()
        {
            if (Interlocked.Exchange(ref attached, 0) == 0)
            {
                return;
            }

            Runtime.ConnectionStateChanged -= OnConnectionStateChanged;
            Runtime.ConversationStateChanged -= OnConversationStateChanged;
        }

        private void OnConnectionStateChanged(
            RelayCove.Shared.Realtime.ConnectionState connectionState) =>
            owner.OnRuntimeConnectionStateChanged(this, connectionState);

        private void OnConversationStateChanged(long revision) =>
            owner.OnRuntimeConversationStateChanged(this, revision);
    }

    private CancellationTokenSource CreateLinkedCancellation(
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        return CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
    }

    private void ThrowIfStopping() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposeStarted) != 0,
            this);

    private static string GetDeviceName() => Environment.MachineName;

    private static string GetClientVersion() =>
        typeof(ClientAccountShellCoordinator).Assembly.GetName().Version?.ToString() ??
        "1.0.0";
}
