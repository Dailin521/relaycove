using Microsoft.Extensions.Logging;
using RelayCove.Client.Attachments;
using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountRuntime : IClientAccountRuntime
{
    private readonly object stateGate = new();
    private readonly ClientAuthenticationSession authenticationSession;
    private readonly IClientAccountRealtimeConnection realtimeConnection;
    private readonly IClientAccountSyncCoordinator syncCoordinator;
    private readonly IClientAccountReadThroughCoordinator readThroughCoordinator;
    private readonly ClientMessageHistoryCoordinator? messageHistoryCoordinator;
    private readonly ClientMentionCandidateCoordinator? mentionCandidateCoordinator;
    private readonly ClientSearchCoordinator? searchCoordinator;
    private readonly ClientMessageSendCoordinator? messageSendCoordinator;
    private readonly IClientAttachmentDownloadCoordinator? attachmentDownloadCoordinator;
    private readonly ClientAutomaticSyncScheduler automaticSyncScheduler;
    private readonly IAsyncDisposable notificationCoordinator;
    private readonly IAsyncDisposable localCache;
    private readonly AccountScopedLocalCache? conversationSource;
    private readonly ClientAccountRuntimeStateHub stateHub;
    private readonly Func<ClientNotificationActivationTarget, bool>?
        notificationTargetAuthorizer;
    private readonly ClientActivityState activityState;
    private readonly ILogger<ClientAccountRuntime> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly HashSet<Task> explicitFlights = [];
    private Task<ClientAccountRuntimeStartOutcome>? startTask;
    private Task<ClientLogoutStatus>? terminalTask;
    private TerminalMode terminalMode;

    internal ClientAccountRuntime(
        AccountScopeIdentity identity,
        ClientAuthenticationSession authenticationSession,
        IClientAccountRealtimeConnection realtimeConnection,
        IClientAccountSyncCoordinator syncCoordinator,
        IClientAccountReadThroughCoordinator readThroughCoordinator,
        IAsyncDisposable? notificationCoordinator,
        IAsyncDisposable localCache,
        ClientActivityState activityState,
        ILogger<ClientAccountRuntime> logger,
        ClientAutomaticSyncScheduler automaticSyncScheduler,
        Func<ClientNotificationActivationTarget, bool>? notificationTargetAuthorizer = null,
        AccountScopedLocalCache? conversationSource = null,
        ClientAccountRuntimeStateHub? stateHub = null,
        ClientMessageHistoryCoordinator? messageHistoryCoordinator = null,
        ClientMessageSendCoordinator? messageSendCoordinator = null,
        ClientMentionCandidateCoordinator? mentionCandidateCoordinator = null,
        IClientAttachmentDownloadCoordinator? attachmentDownloadCoordinator = null,
        ClientSearchCoordinator? searchCoordinator = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.authenticationSession = authenticationSession ??
            throw new ArgumentNullException(nameof(authenticationSession));
        this.realtimeConnection = realtimeConnection ??
            throw new ArgumentNullException(nameof(realtimeConnection));
        this.syncCoordinator = syncCoordinator ??
            throw new ArgumentNullException(nameof(syncCoordinator));
        this.readThroughCoordinator = readThroughCoordinator ??
            throw new ArgumentNullException(nameof(readThroughCoordinator));
        this.notificationCoordinator = notificationCoordinator ??
            new NoOpClientNotificationRoundCoordinator();
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.conversationSource = conversationSource;
        this.messageHistoryCoordinator = messageHistoryCoordinator;
        this.messageSendCoordinator = messageSendCoordinator;
        this.mentionCandidateCoordinator = mentionCandidateCoordinator;
        this.searchCoordinator = searchCoordinator;
        this.attachmentDownloadCoordinator = attachmentDownloadCoordinator;
        this.automaticSyncScheduler = automaticSyncScheduler ??
            throw new ArgumentNullException(nameof(automaticSyncScheduler));
        this.activityState = activityState ?? throw new ArgumentNullException(nameof(activityState));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.stateHub = stateHub ?? new ClientAccountRuntimeStateHub(logger);
        this.notificationTargetAuthorizer = notificationTargetAuthorizer;
        if (!authenticationSession.IsAuthenticated ||
            authenticationSession.UserId != identity.UserId ||
            !Equals(authenticationSession.ServerBaseUri, identity.CanonicalServerBaseUri))
        {
            throw new ArgumentException(
                "The authentication session must match the account scope identity.",
                nameof(authenticationSession));
        }
    }

    public AccountScopeIdentity Identity { get; }

    public event Action<ConnectionState> ConnectionStateChanged
    {
        add => stateHub.ConnectionStateChanged += value;
        remove => stateHub.ConnectionStateChanged -= value;
    }

    public event Action<long> ConversationStateChanged
    {
        add => stateHub.ConversationStateChanged += value;
        remove => stateHub.ConversationStateChanged -= value;
    }

    public ConnectionState ConnectionState => realtimeConnection.State;

    public override string ToString() =>
        $"{nameof(ClientAccountRuntime)} {{ Identity = [REDACTED], " +
        $"ConnectionState = {ConnectionState} }}";

    public bool TryAuthorizeNotificationTarget(ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (stateGate)
        {
            if (terminalMode != TerminalMode.None ||
                !authenticationSession.IsAuthenticated ||
                !string.Equals(Identity.Id, target.AccountScopeId, StringComparison.Ordinal) ||
                notificationTargetAuthorizer is null)
            {
                return false;
            }

            try
            {
                return notificationTargetAuthorizer(target);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Authorizing a notification target failed; errorType={ErrorType}.",
                    exception.GetType().Name);
                return false;
            }
        }
    }

    public void UpdateActivity(ClientActivitySnapshot snapshot)
    {
        lock (stateGate)
        {
            ThrowIfTerminating();
            activityState.Update(snapshot);
            automaticSyncScheduler.UpdateActivity(snapshot.IsMainWindowForeground);
        }
    }

    public Task<LocalConversationListReadOutcome> ReadConversationListAsync(
        CancellationToken cancellationToken = default)
    {
        lock (stateGate)
        {
            ThrowIfTerminating();
        }

        return conversationSource is null
            ? Task.FromResult(LocalConversationListReadOutcome.Failure(
                LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
                revision: 0))
            : conversationSource.ReadConversationListAsync(cancellationToken);
    }

    public Task<LocalMessagePageReadOutcome> ReadMessagePageAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            token => conversationSource is null
                ? Task.FromResult(LocalMessagePageReadOutcome.Failure(
                    LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
                    conversationId))
                : conversationSource.ReadMessagePageAsync(
                    conversationId,
                    beforeMessageId,
                    limit,
                    token),
            cancellationToken);

    public Task<ClientMessageHistoryPageOutcome> LoadMessageHistoryAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            token => messageHistoryCoordinator is null
                ? Task.FromResult(ClientMessageHistoryPageOutcome.Failure(
                    ClientMessageLoadStatus.LocalCacheFailure))
                : messageHistoryCoordinator.LoadHistoryAsync(
                    conversationId,
                    beforeMessageId,
                    limit,
                    token),
            cancellationToken);

    public Task<ClientMessageAroundOutcome> LoadMessageAroundAsync(
        Guid conversationId,
        long messageId,
        int before,
        int after,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            token => messageHistoryCoordinator is null
                ? Task.FromResult(ClientMessageAroundOutcome.Failure(
                    ClientMessageLoadStatus.LocalCacheFailure))
                : messageHistoryCoordinator.LoadAroundAsync(
                    conversationId,
                    messageId,
                    before,
                    after,
                    token),
            cancellationToken);

    public Task<ClientMentionCandidateOutcome> SearchMentionCandidatesAsync(
        Guid conversationId,
        string? query,
        int limit = ClientMentionCandidateCoordinator.DefaultLimit,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            token => mentionCandidateCoordinator is null
                ? Task.FromResult(ClientMentionCandidateOutcome.Failure(
                    ClientMentionCandidateStatus.LocalCacheFailure))
                : mentionCandidateCoordinator.SearchAsync(
                    conversationId,
                    query,
                    limit,
                    token),
            cancellationToken);

    public Task<ClientSearchOutcome> SearchMessagesAsync(
        string? keyword,
        Guid? conversationId,
        int limit = ClientSearchCoordinator.DefaultLimit,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            token => searchCoordinator is null
                ? Task.FromResult(ClientSearchOutcome.Failure(
                    ClientSearchStatus.LocalCacheFailure))
                : searchCoordinator.SearchAsync(keyword, conversationId, limit, token),
            cancellationToken);

    public Task<ClientMessageSendOutcome> SendTextMessageAsync(
        Guid conversationId,
        string? content,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            token => messageSendCoordinator is null
                ? Task.FromResult(ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.LocalCacheFailure))
                : messageSendCoordinator.SendTextAsync(
                    conversationId,
                    content,
                    replyToMessageId,
                    mentionUserIds,
                    token),
            cancellationToken);

    public Task<ClientMessageSendOutcome> SendAttachmentsAsync(
        Guid conversationId,
        MessageType type,
        IReadOnlyList<ClientAttachmentUploadSource>? sources,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentSendProgress>? progress = null) =>
        TrackRuntimeOperation(
            token => messageSendCoordinator is null
                ? Task.FromResult(ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.LocalCacheFailure))
                : messageSendCoordinator.SendAttachmentsAsync(
                    conversationId,
                    type,
                    sources,
                    replyToMessageId,
                    mentionUserIds,
                    token,
                    progress),
            cancellationToken);

    public Task<ClientMessageSendOutcome> RetryPendingMessageAsync(
        Guid conversationId,
        Guid clientMessageId,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            token => messageSendCoordinator is null
                ? Task.FromResult(ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.LocalCacheFailure))
                : messageSendCoordinator.RetryAsync(
                    conversationId,
                    clientMessageId,
                    token),
            cancellationToken);

    public Task<ClientAttachmentDownloadOutcome> DownloadAttachmentAsync(
        Guid conversationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentDownloadProgress>? progress = null) =>
        TrackRuntimeOperation(
            token => attachmentDownloadCoordinator is null
                ? Task.FromResult(ClientAttachmentDownloadOutcome.Failure(
                    ClientAttachmentDownloadStatus.LocalCacheFailure))
                : attachmentDownloadCoordinator.DownloadAsync(
                    conversationId,
                    attachmentId,
                    token,
                    progress),
            cancellationToken);

    public Task<ClientAttachmentImageLoadOutcome> LoadAttachmentImageAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentImageRendition rendition,
        ClientAttachmentImageCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return TrackRuntimeOperation(
            token => attachmentDownloadCoordinator is null
                ? Task.FromResult(ClientAttachmentImageLoadOutcome.Failure(
                    ClientAttachmentImageLoadStatus.LocalCacheFailure))
                : attachmentDownloadCoordinator.LoadImageAsync(
                    conversationId,
                    attachmentId,
                    rendition,
                    commit,
                    token),
            cancellationToken);
    }

    public Task<ClientAttachmentRevealOutcome> RevealAttachmentInFolderAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentRevealCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(commit);
        var revealStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ClientAttachmentRevealOutcome> flight;
        lock (stateGate)
        {
            ThrowIfTerminating();
            flight = RunRevealOperationAsync(
                (token, revealCommit) => attachmentDownloadCoordinator is null
                    ? Task.FromResult(ClientAttachmentRevealOutcome.FromStatus(
                        ClientAttachmentRevealStatus.LocalCacheFailure))
                    : attachmentDownloadCoordinator.RevealInFolderAsync(
                        conversationId,
                        attachmentId,
                        revealCommit,
                        token),
                cancellationToken,
                lifetimeCancellation.Token,
                commit,
                revealStarted);
            // A reveal remains a regular cancellable runtime flight until its
            // commit callback marks native Shell start. From that point a pinned
            // file capability may keep the Shell call alive, but termination must
            // not wait for the unbounded external operation.
            TrackExplicitFlight(revealStarted.Task);
        }

        return flight;
    }

    public Task<ClientAttachmentOpenOutcome> OpenAttachmentAsync(
        Guid conversationId,
        Guid attachmentId,
        IntPtr ownerWindow,
        ClientAttachmentOpenCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(commit);
        var openStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ClientAttachmentOpenOutcome> flight;
        lock (stateGate)
        {
            ThrowIfTerminating();
            flight = RunOpenOperationAsync(
                (token, openCommit) => attachmentDownloadCoordinator is null
                    ? Task.FromResult(ClientAttachmentOpenOutcome.FromStatus(
                        ClientAttachmentOpenStatus.LocalFailure))
                    : attachmentDownloadCoordinator.OpenAsync(
                        conversationId,
                        attachmentId,
                        ownerWindow,
                        openCommit,
                        token),
                cancellationToken,
                lifetimeCancellation.Token,
                commit,
                openStarted);
            // The open operation becomes external only after the coordinator's
            // exact confirmation commits the already-handoff STA job. Termination
            // waits for the pre-commit work but never for Attachment Manager.
            TrackExplicitFlight(openStarted.Task);
        }

        return flight;
    }

    public Task<LocalCacheOperationStatus> MarkConversationRenderedThroughAsync(
        Guid conversationId,
        long messageId,
        CancellationToken cancellationToken = default) =>
        TrackRuntimeOperation(
            async token =>
            {
                if (conversationSource is null)
                {
                    return LocalCacheOperationStatus.AuthoritativeSnapshotRequired;
                }

                var status = await conversationSource
                    .MarkConversationRenderedThroughAsync(
                        conversationId,
                        messageId,
                        token)
                    .ConfigureAwait(false);
                if (status == LocalCacheOperationStatus.Ready)
                {
                    RequestReadThroughUpload();
                }

                return status;
            },
            cancellationToken);

    public Task<ClientAccountRuntimeStartOutcome> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ClientAccountRuntimeStartOutcome> sharedStart;
        lock (stateGate)
        {
            ThrowIfTerminating();
            if (startTask is null)
            {
                var completion = new TaskCompletionSource<ClientAccountRuntimeStartOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                startTask = completion.Task;
                _ = CompleteStartAsync(completion);
            }

            sharedStart = startTask;
        }

        return cancellationToken.CanBeCanceled
            ? sharedStart.WaitAsync(cancellationToken)
            : sharedStart;
    }

    public Task<ClientSyncRunOutcome> TriggerSyncAsync(
        SyncReason reason,
        CancellationToken cancellationToken = default)
    {
        if (reason is not SyncReason.WindowActivated and not SyncReason.Periodic)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Only window-activation and periodic sync may be triggered explicitly.");
        }

        TaskCompletionSource<ClientSyncRunOutcome> completion;
        CancellationToken lifetimeToken;
        lock (stateGate)
        {
            ThrowIfTerminating();
            if (startTask?.IsCompletedSuccessfully != true)
            {
                throw new InvalidOperationException(
                    "The account runtime must finish starting before sync is triggered.");
            }

            completion = new TaskCompletionSource<ClientSyncRunOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lifetimeToken = lifetimeCancellation.Token;
            TrackExplicitFlight(completion.Task);
        }

        _ = CompleteExplicitSyncAsync(reason, lifetimeToken, completion);
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public Task<ClientSyncRunOutcome> RetryRealtimeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<ClientSyncRunOutcome> completion;
        CancellationToken lifetimeToken;
        lock (stateGate)
        {
            ThrowIfTerminating();
            if (startTask?.IsCompletedSuccessfully != true)
            {
                throw new InvalidOperationException(
                    "The account runtime must finish starting before realtime is retried.");
            }

            completion = new TaskCompletionSource<ClientSyncRunOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lifetimeToken = lifetimeCancellation.Token;
            TrackExplicitFlight(completion.Task);
        }

        _ = CompleteRetryAsync(lifetimeToken, completion);
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public Task<ClientLogoutStatus> LogoutAsync(
        CancellationToken cancellationToken = default)
    {
        Task<ClientLogoutStatus> sharedTerminal;
        lock (stateGate)
        {
            if (terminalMode == TerminalMode.Dispose)
            {
                throw new ObjectDisposedException(nameof(ClientAccountRuntime));
            }

            if (terminalMode == TerminalMode.None)
            {
                terminalMode = TerminalMode.Logout;
                StartTermination(logout: true);
            }

            sharedTerminal = terminalTask!;
        }

        return cancellationToken.CanBeCanceled
            ? sharedTerminal.WaitAsync(cancellationToken)
            : sharedTerminal;
    }

    public ValueTask DisposeAsync()
    {
        Task<ClientLogoutStatus> sharedTerminal;
        lock (stateGate)
        {
            if (terminalMode == TerminalMode.None)
            {
                terminalMode = TerminalMode.Dispose;
                StartTermination(logout: false);
            }

            sharedTerminal = terminalTask!;
        }

        return new ValueTask(sharedTerminal);
    }

    private async Task CompleteStartAsync(
        TaskCompletionSource<ClientAccountRuntimeStartOutcome> completion)
    {
        try
        {
            completion.TrySetResult(await StartCoreAsync().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void StartTermination(bool logout)
    {
        stateHub.Stop();
        var completion = new TaskCompletionSource<ClientLogoutStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        terminalTask = completion.Task;
        _ = CompleteTerminationAsync(logout, completion);
    }

    private async Task CompleteTerminationAsync(
        bool logout,
        TaskCompletionSource<ClientLogoutStatus> completion)
    {
        try
        {
            completion.TrySetResult(await TerminateAsync(logout).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<ClientAccountRuntimeStartOutcome> StartCoreAsync()
    {
        try
        {
            await realtimeConnection.StartAsync(lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return CanceledStartOutcome();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Initial realtime connection failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }

        if (lifetimeCancellation.IsCancellationRequested)
        {
            return CanceledStartOutcome();
        }

        ClientSyncRunOutcome syncOutcome;
        try
        {
            syncOutcome = await syncCoordinator
                .TriggerAsync(SyncReason.Startup, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (lifetimeCancellation.IsCancellationRequested)
        {
            syncOutcome = CanceledSyncOutcome(SyncReason.Startup);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            syncOutcome = CanceledSyncOutcome(SyncReason.Startup);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Initial account sync failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            syncOutcome = new ClientSyncRunOutcome(
                ClientSyncRunStatus.LocalCacheFailure,
                SyncReason.Startup,
                RoundsExecuted: 0);
        }

        var outcome = new ClientAccountRuntimeStartOutcome(
            realtimeConnection.State,
            syncOutcome);
        lock (stateGate)
        {
            if (terminalMode == TerminalMode.None &&
                !lifetimeCancellation.IsCancellationRequested)
            {
                automaticSyncScheduler.Start(
                    activityState.Snapshot.IsMainWindowForeground);
            }
        }

        logger.LogInformation(
            "Account runtime start completed; realtimeState={RealtimeState}; " +
            "syncStatus={SyncStatus}.",
            outcome.RealtimeState,
            outcome.StartupSyncOutcome.Status);
        return outcome;
    }

    private async Task CompleteExplicitSyncAsync(
        SyncReason reason,
        CancellationToken lifetimeToken,
        TaskCompletionSource<ClientSyncRunOutcome> completion)
    {
        try
        {
            completion.TrySetResult(await RunExplicitSyncAsync(reason, lifetimeToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task CompleteRetryAsync(
        CancellationToken lifetimeToken,
        TaskCompletionSource<ClientSyncRunOutcome> completion)
    {
        try
        {
            completion.TrySetResult(await RetryRealtimeCoreAsync(lifetimeToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<ClientSyncRunOutcome> RetryRealtimeCoreAsync(
        CancellationToken lifetimeToken)
    {
        if (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(SyncReason.Reconnect);
        }

        try
        {
            await realtimeConnection.StartAsync(lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(SyncReason.Reconnect);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Realtime retry failed; errorType={ErrorType}; continuing with reconnect sync.",
                exception.GetType().Name);
        }

        return await RunExplicitSyncAsync(SyncReason.Reconnect, lifetimeToken)
            .ConfigureAwait(false);
    }

    private async Task<ClientSyncRunOutcome> RunExplicitSyncAsync(
        SyncReason reason,
        CancellationToken lifetimeToken)
    {
        if (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(reason);
        }

        try
        {
            return await syncCoordinator.TriggerAsync(reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(reason);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(reason);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Explicit account sync failed unexpectedly; reason={Reason}; errorType={ErrorType}.",
                reason,
                exception.GetType().Name);
            return new ClientSyncRunOutcome(
                ClientSyncRunStatus.LocalCacheFailure,
                reason,
                RoundsExecuted: 0);
        }
    }

    private async Task<ClientLogoutStatus> TerminateAsync(bool logout)
    {
        var failures = new List<Exception>();
        try
        {
            lifetimeCancellation.Cancel();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await CaptureFailureAsync(() => automaticSyncScheduler.DisposeAsync(), failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(() => realtimeConnection.DisposeAsync(), failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(() => syncCoordinator.DisposeAsync(), failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(() => readThroughCoordinator.DisposeAsync(), failures)
            .ConfigureAwait(false);
        if (messageHistoryCoordinator is not null)
        {
            await CaptureFailureAsync(
                    () => messageHistoryCoordinator.DisposeAsync(),
                    failures)
                .ConfigureAwait(false);
        }
        if (mentionCandidateCoordinator is not null)
        {
            await CaptureFailureAsync(
                    () => mentionCandidateCoordinator.DisposeAsync(),
                    failures)
                .ConfigureAwait(false);
        }
        if (searchCoordinator is not null)
        {
            await CaptureFailureAsync(() => searchCoordinator.DisposeAsync(), failures)
                .ConfigureAwait(false);
        }
        if (messageSendCoordinator is not null)
        {
            await CaptureFailureAsync(
                    () => messageSendCoordinator.DisposeAsync(),
                    failures)
                .ConfigureAwait(false);
        }
        if (attachmentDownloadCoordinator is not null)
        {
            await CaptureFailureAsync(
                    () => attachmentDownloadCoordinator.DisposeAsync(),
                    failures)
                .ConfigureAwait(false);
        }
        await CaptureFailureAsync(() => notificationCoordinator.DisposeAsync(), failures)
            .ConfigureAwait(false);

        Task<ClientAccountRuntimeStartOutcome>? startup;
        Task[] explicitOperations;
        lock (stateGate)
        {
            startup = startTask;
            explicitOperations = [.. explicitFlights];
        }

        if (startup is not null)
        {
            await CaptureFlightFailureAsync(startup, failures)
                .ConfigureAwait(false);
        }

        foreach (var explicitOperation in explicitOperations)
        {
            await CaptureFlightFailureAsync(explicitOperation, failures)
                .ConfigureAwait(false);
        }

        await CaptureFailureAsync(() => localCache.DisposeAsync(), failures)
            .ConfigureAwait(false);

        var logoutStatus = ClientLogoutStatus.LoggedOut;
        if (logout)
        {
            try
            {
                logoutStatus = await authenticationSession
                    .LogoutAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        await CaptureFailureAsync(() => authenticationSession.DisposeAsync(), failures)
            .ConfigureAwait(false);
        lifetimeCancellation.Dispose();

        logger.LogInformation(
            "Account runtime terminated; mode={Mode}; logoutStatus={LogoutStatus}; " +
            "cleanupFailures={CleanupFailures}.",
            logout ? TerminalMode.Logout : TerminalMode.Dispose,
            logout ? logoutStatus.ToString() : "NotRequested",
            failures.Count);

        if (failures.Count != 0)
        {
            throw new AggregateException("Account runtime cleanup failed.", failures);
        }

        return logoutStatus;
    }

    private static async Task CaptureFailureAsync(
        Func<ValueTask> operation,
        ICollection<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureFlightFailureAsync(
        Task operation,
        ICollection<Exception> failures)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Termination has already canceled the runtime lifetime token.
            // A tracked startup or explicit operation acknowledging that token
            // is expected convergence, not a cleanup failure.
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private ClientAccountRuntimeStartOutcome CanceledStartOutcome() =>
        new(realtimeConnection.State, CanceledSyncOutcome(SyncReason.Startup));

    private static ClientSyncRunOutcome CanceledSyncOutcome(SyncReason reason) =>
        new(ClientSyncRunStatus.Canceled, reason, RoundsExecuted: 0);

    private void TrackExplicitFlight(Task flight)
    {
        explicitFlights.Add(flight);
        _ = flight.ContinueWith(
            static (completed, state) =>
                ((ClientAccountRuntime)state!).RemoveExplicitFlight(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task<T> TrackRuntimeOperation<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken callerCancellation)
    {
        callerCancellation.ThrowIfCancellationRequested();
        Task<T> flight;
        lock (stateGate)
        {
            ThrowIfTerminating();
            flight = RunRuntimeOperationAsync(
                operation,
                callerCancellation,
                lifetimeCancellation.Token);
            TrackExplicitFlight(flight);
        }

        return flight;
    }

    private static async Task<T> RunRuntimeOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken callerCancellation,
        CancellationToken lifetimeToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            lifetimeToken);
        return await operation(linkedCancellation.Token).ConfigureAwait(false);
    }

    private static async Task<ClientAttachmentRevealOutcome> RunRevealOperationAsync(
        Func<CancellationToken, ClientAttachmentRevealCommit,
            Task<ClientAttachmentRevealOutcome>> operation,
        CancellationToken callerCancellation,
        CancellationToken lifetimeToken,
        ClientAttachmentRevealCommit commit,
        TaskCompletionSource revealStarted)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            lifetimeToken);
        var committed = false;
        ClientAttachmentRevealStatus Commit()
        {
            try
            {
                return commit();
            }
            finally
            {
                committed = true;
                revealStarted.TrySetResult();
            }
        }

        try
        {
            return await operation(linkedCancellation.Token, Commit).ConfigureAwait(false);
        }
        finally
        {
            // A validation failure or cancellation before commit must still let
            // runtime termination make progress. A post-commit native call has
            // already completed this signal in Commit().
            if (!committed)
            {
                revealStarted.TrySetResult();
            }
        }
    }

    private static async Task<ClientAttachmentOpenOutcome> RunOpenOperationAsync(
        Func<CancellationToken, ClientAttachmentOpenCommit,
            Task<ClientAttachmentOpenOutcome>> operation,
        CancellationToken callerCancellation,
        CancellationToken lifetimeToken,
        ClientAttachmentOpenCommit commit,
        TaskCompletionSource openStarted)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            lifetimeToken);
        var handedToWindows = false;
        ClientAttachmentOpenStatus Commit(Func<bool> commitPreparedJob)
        {
            var status = commit(commitPreparedJob);
            if (status == ClientAttachmentOpenStatus.HandedToWindows)
            {
                handedToWindows = true;
                openStarted.TrySetResult();
            }

            return status;
        }

        try
        {
            return await operation(linkedCancellation.Token, Commit).ConfigureAwait(false);
        }
        finally
        {
            // Only a successful commit hands an already-started STA operation
            // to Windows. Every other commit result, and a throwing callback,
            // remains pre-commit work that termination must await until the
            // coordinator has completed its cleanup.
            if (!handedToWindows)
            {
                openStarted.TrySetResult();
            }
        }
    }

    private void RequestReadThroughUpload()
    {
        try
        {
            _ = ObserveReadThroughUploadAsync(
                readThroughCoordinator.TriggerAsync(CancellationToken.None));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Requesting read-through after a rendered message boundary failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private async Task ObserveReadThroughUploadAsync(
        Task<ClientReadThroughRunOutcome> upload)
    {
        try
        {
            var outcome = await upload.ConfigureAwait(false);
            if (outcome.Status is not ClientReadThroughRunStatus.Completed and
                not ClientReadThroughRunStatus.Canceled)
            {
                logger.LogWarning(
                    "Rendered-message read-through upload did not complete; status={Status}.",
                    outcome.Status);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Rendered-message read-through upload failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void RemoveExplicitFlight(Task flight)
    {
        lock (stateGate)
        {
            explicitFlights.Remove(flight);
        }
    }

    private void ThrowIfTerminating()
    {
        if (terminalMode != TerminalMode.None)
        {
            throw new ObjectDisposedException(nameof(ClientAccountRuntime));
        }
    }

    private enum TerminalMode
    {
        None,
        Dispose,
        Logout,
    }
}
