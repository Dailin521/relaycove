using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;
using RelayCove.Client.Attachments;
using RelayCove.Client.Auth;
using RelayCove.Client.Mentions;
using RelayCove.Client.Search;
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
    private IReadOnlyList<SearchResultDto> activeSearchResults =
        Array.Empty<SearchResultDto>();
    private CancellationTokenSource? searchFlightCancellation;
    private CancellationTokenSource? navigationFlightCancellation;
    private ClientSearchScope? activeSearchScope;
    private long searchSerial;
    private long navigationSerial;
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

    // Results are deliberately not carried in the invalidation event. Consumers must
    // clear their own view immediately rather than retaining a payload from an old lease.
    public event Action? SearchResultsInvalidated;

    public ClientAccountShellSnapshot Snapshot => Volatile.Read(ref snapshot);

    public LocalConversationListReadOutcome ConversationList =>
        Volatile.Read(ref conversationList);

    public ClientMessageListSnapshot MessageList => Volatile.Read(ref messageList);

    public IReadOnlyList<SearchResultDto> SearchResults
    {
        get
        {
            lock (stateGate)
            {
                return activeSearchResults;
            }
        }
    }

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
            ClearActiveOwnership(out var lease, out var detachedRuntime, out var searchInvalidation);
            CompleteSearchInvalidation(searchInvalidation);
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
        SearchInvalidation searchInvalidation;
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
            searchInvalidation = InvalidateCurrentSearchResultsLocked();
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

        CompleteSearchInvalidation(searchInvalidation);
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

    public void InvalidateSearchResults()
    {
        SearchInvalidation invalidation;
        lock (stateGate)
        {
            invalidation = InvalidateSearchResultsLocked(forcePublishHandlers: true);
        }

        CompleteSearchInvalidation(invalidation);
    }

    public async Task<ClientSearchOutcome> SearchMessagesAsync(
        string? keyword,
        ClientSearchScope scope,
        int limit = ClientSearchCoordinator.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(scope))
        {
            return ClientSearchOutcome.Failure(ClientSearchStatus.ValidationFailed);
        }

        RuntimeSubscription? subscription;
        MessageSelection? selection = null;
        Guid? conversationId;
        long serial;
        CancellationTokenSource? previousSearch = null;
        CancellationTokenSource? previousNavigation = null;
        CancellationTokenSource flightCancellation;
        lock (stateGate)
        {
            if (runtime is null || runtimeSubscription is null ||
                Volatile.Read(ref disposeStarted) != 0)
            {
                return ClientSearchOutcome.Failure(ClientSearchStatus.Unavailable);
            }

            if (scope == ClientSearchScope.CurrentConversation)
            {
                selection = messageSelection;
                if (selection is null ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready)
                {
                    return ClientSearchOutcome.Failure(ClientSearchStatus.Unavailable);
                }

                conversationId = selection.ConversationId;
            }
            else
            {
                conversationId = null;
            }

            previousSearch = searchFlightCancellation;
            previousNavigation = navigationFlightCancellation;
            searchFlightCancellation = flightCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    lifetimeCancellation.Token,
                    cancellationToken);
            navigationFlightCancellation = null;
            activeSearchResults = Array.Empty<SearchResultDto>();
            activeSearchScope = scope;
            serial = ++searchSerial;
            ++navigationSerial;
            subscription = runtimeSubscription;
        }

        CancelSearchFlight(previousSearch);
        CancelSearchFlight(previousNavigation);

        try
        {
            var outcome = await subscription.Runtime
                .SearchMessagesAsync(
                    keyword,
                    conversationId,
                    limit,
                    flightCancellation.Token)
                .ConfigureAwait(false);
            if (outcome.Status == ClientSearchStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(subscription.Runtime)
                    .ConfigureAwait(false);
                return outcome;
            }

            lock (stateGate)
            {
                if (!IsCurrentSearchLeaseLocked(
                        subscription,
                        selection,
                        scope,
                        serial,
                        flightCancellation) ||
                    !AreSearchResultsAuthorizedLocked(outcome.Results))
                {
                    return ClientSearchOutcome.Failure(ClientSearchStatus.Stale);
                }

                if (outcome.Status == ClientSearchStatus.Completed)
                {
                    activeSearchResults = outcome.Results;
                }
                else
                {
                    activeSearchScope = null;
                }

                searchFlightCancellation = null;
                return outcome;
            }
        }
        catch (OperationCanceledException)
        {
            lock (stateGate)
            {
                return IsCurrentSearchLeaseLocked(
                    subscription,
                    selection,
                    scope,
                    serial,
                    flightCancellation)
                    ? ClientSearchOutcome.Failure(ClientSearchStatus.Canceled)
                    : ClientSearchOutcome.Failure(ClientSearchStatus.Stale);
            }
        }
        catch (ObjectDisposedException)
        {
            return ClientSearchOutcome.Failure(ClientSearchStatus.Stale);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Searching messages through the active account failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientSearchOutcome.Failure(ClientSearchStatus.LocalCacheFailure);
        }
        finally
        {
            lock (stateGate)
            {
                if (ReferenceEquals(searchFlightCancellation, flightCancellation))
                {
                    searchFlightCancellation = null;
                }
            }

            flightCancellation.Dispose();
        }
    }

    public async Task<ClientSearchNavigationOutcome> NavigateSearchResultAsync(
        SearchResultDto result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        RuntimeSubscription? subscription;
        long currentSearchSerial;
        long currentNavigationSerial;
        CancellationTokenSource? previousNavigation;
        CancellationTokenSource flightCancellation;
        lock (stateGate)
        {
            subscription = runtimeSubscription;
            if (subscription is null || runtime is null ||
                Volatile.Read(ref disposeStarted) != 0 ||
                !ContainsActiveSearchResultLocked(result) ||
                !IsConversationAuthorizedLocked(result.ConversationId))
            {
                return ClientSearchNavigationOutcome.Failure(
                    ClientSearchNavigationStatus.Unavailable);
            }

            previousNavigation = navigationFlightCancellation;
            navigationFlightCancellation = flightCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    lifetimeCancellation.Token,
                    cancellationToken);
            currentSearchSerial = searchSerial;
            currentNavigationSerial = ++navigationSerial;
        }

        CancelSearchFlight(previousNavigation);
        try
        {
            var around = await subscription.Runtime
                .LoadMessageAroundAsync(
                    result.ConversationId,
                    result.MessageId,
                    before: 20,
                    after: 20,
                    flightCancellation.Token)
                .ConfigureAwait(false);
            if (around.Status == ClientMessageLoadStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(subscription.Runtime)
                    .ConfigureAwait(false);
                return ClientSearchNavigationOutcome.Failure(
                    ClientSearchNavigationStatus.AuthenticationRequired);
            }

            MessageSelection? previousSelection;
            MessageSelection? nextSelection;
            ClientActivitySnapshot activity;
            SearchInvalidation searchInvalidation;
            lock (stateGate)
            {
                if (!IsCurrentNavigationLeaseLocked(
                        subscription,
                        result,
                        currentSearchSerial,
                        currentNavigationSerial,
                        flightCancellation))
                {
                    return ClientSearchNavigationOutcome.Failure(
                        ClientSearchNavigationStatus.Stale);
                }

                if (!IsCompletedAroundForSearchResult(around, result))
                {
                    return ClientSearchNavigationOutcome.Failure(
                        around.Status == ClientMessageLoadStatus.Completed
                            ? ClientSearchNavigationStatus.ProtocolError
                            : MapSearchNavigationStatus(around.Status));
                }

                searchInvalidation = InvalidateSearchResultsLocked(publishHandlers: false);
                previousSelection = messageSelection;
                nextSelection = new MessageSelection(
                    result.ConversationId,
                    result.MessageId,
                    subscription,
                    CancellationTokenSource.CreateLinkedTokenSource(
                        lifetimeCancellation.Token),
                    around);
                messageSelection = nextSelection;
                renderedConversationId = null;
                activity = BuildRuntimeActivityLocked();
            }

            CompleteSearchInvalidation(searchInvalidation);
            previousSelection?.Cancel();
            PublishMessageList(CreateMessageSnapshot(
                nextSelection,
                ClientMessageListStatus.Loading,
                isLoading: true,
                lastLoadStatus: null));
            TryUpdateRuntimeActivity(subscription.Runtime, activity);
            _ = OpenMessageSelectionAsync(nextSelection);
            return new ClientSearchNavigationOutcome(ClientSearchNavigationStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            lock (stateGate)
            {
                return IsCurrentNavigationLeaseLocked(
                    subscription,
                    result,
                    currentSearchSerial,
                    currentNavigationSerial,
                    flightCancellation)
                    ? ClientSearchNavigationOutcome.Failure(ClientSearchNavigationStatus.Canceled)
                    : ClientSearchNavigationOutcome.Failure(ClientSearchNavigationStatus.Stale);
            }
        }
        catch (ObjectDisposedException)
        {
            return ClientSearchNavigationOutcome.Failure(ClientSearchNavigationStatus.Stale);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Navigating to a search result failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientSearchNavigationOutcome.Failure(
                ClientSearchNavigationStatus.LocalCacheFailure);
        }
        finally
        {
            lock (stateGate)
            {
                if (ReferenceEquals(navigationFlightCancellation, flightCancellation))
                {
                    navigationFlightCancellation = null;
                }
            }

            flightCancellation.Dispose();
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

    public async Task<ClientMessageSendOutcome> SendTextMessageAsync(
        string? content,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!ClientMentionPolicy.TryCanonicalizeUserIds(
                mentionUserIds ?? Array.Empty<Guid>(),
                out var canonicalMentionUserIds))
        {
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed);
        }

        IClientAccountRuntime? activeRuntime;
        Guid conversationId;
        lock (stateGate)
        {
            if (messageSelection is not { } selection ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                replyToMessageId is <= 0 ||
                (replyToMessageId.HasValue &&
                 !selection.Messages.ContainsKey(replyToMessageId.Value)))
            {
                return ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.Unavailable);
            }

            activeRuntime = runtime;
            conversationId = selection.ConversationId;
        }

        if (activeRuntime is null)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Unavailable);
        }

        try
        {
            var outcome = await activeRuntime
                .SendTextMessageAsync(
                    conversationId,
                    content,
                    replyToMessageId,
                    canonicalMentionUserIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Status == ClientMessageSendStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(activeRuntime)
                    .ConfigureAwait(false);
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Sending a Text message through the active account failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.LocalCacheFailure);
        }
    }

    public async Task<ClientMessageSendOutcome> SendAttachmentsAsync(
        MessageType type,
        IReadOnlyList<ClientAttachmentUploadSource>? sources,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentSendProgress>? progress = null)
    {
        if (!ClientMentionPolicy.TryCanonicalizeUserIds(
                mentionUserIds ?? Array.Empty<Guid>(),
                out var canonicalMentionUserIds))
        {
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed);
        }

        IClientAccountRuntime? activeRuntime;
        Guid conversationId;
        lock (stateGate)
        {
            if (messageSelection is not { } selection ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                replyToMessageId is <= 0 ||
                (replyToMessageId.HasValue &&
                 !selection.Messages.ContainsKey(replyToMessageId.Value)))
            {
                return ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.Unavailable);
            }

            activeRuntime = runtime;
            conversationId = selection.ConversationId;
        }

        if (activeRuntime is null)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Unavailable);
        }

        try
        {
            var outcome = await activeRuntime
                .SendAttachmentsAsync(
                    conversationId,
                    type,
                    sources,
                    replyToMessageId,
                    canonicalMentionUserIds,
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
            if (outcome.Status == ClientMessageSendStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(activeRuntime)
                    .ConfigureAwait(false);
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Sending an attachment message through the active account failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.LocalCacheFailure);
        }
    }

    public async Task<ClientMentionCandidateOutcome> SearchMentionCandidatesAsync(
        string? query,
        int limit = ClientMentionCandidateCoordinator.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        IClientAccountRuntime? activeRuntime;
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready)
            {
                return ClientMentionCandidateOutcome.Failure(
                    ClientMentionCandidateStatus.Unavailable);
            }

            activeRuntime = runtime;
        }

        if (activeRuntime is null)
        {
            return ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.Unavailable);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            selection.Token,
            lifetimeCancellation.Token);
        try
        {
            var outcome = await activeRuntime
                .SearchMentionCandidatesAsync(
                    selection.ConversationId,
                    query,
                    limit,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (outcome.Status == ClientMentionCandidateStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(activeRuntime)
                    .ConfigureAwait(false);
                return outcome;
            }

            lock (stateGate)
            {
                if (!ReferenceEquals(runtime, activeRuntime) ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready)
                {
                    return ClientMentionCandidateOutcome.Failure(
                        ClientMentionCandidateStatus.Stale);
                }
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            lock (stateGate)
            {
                return IsCurrentMessageSelectionLocked(selection)
                    ? ClientMentionCandidateOutcome.Failure(
                        ClientMentionCandidateStatus.Canceled)
                    : ClientMentionCandidateOutcome.Failure(
                        ClientMentionCandidateStatus.Stale);
            }
        }
        catch (ObjectDisposedException)
        {
            return ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.Stale);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Searching mention candidates through the active account failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.LocalCacheFailure);
        }
    }

    public async Task<ClientAttachmentDownloadOutcome> DownloadAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentDownloadProgress>? progress = null)
    {
        if (attachmentId == Guid.Empty)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.AttachmentUnavailable);
        }

        IClientAccountRuntime? activeRuntime;
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                !SelectionContainsAttachment(selection, attachmentId))
            {
                return ClientAttachmentDownloadOutcome.Failure(
                    ClientAttachmentDownloadStatus.AttachmentUnavailable);
            }

            activeRuntime = runtime;
        }

        if (activeRuntime is null)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.AttachmentUnavailable);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            selection.Token,
            lifetimeCancellation.Token);
        try
        {
            var outcome = await activeRuntime
                .DownloadAttachmentAsync(
                    selection.ConversationId,
                    attachmentId,
                    linkedCancellation.Token,
                    progress)
                .ConfigureAwait(false);
            if (outcome.Status == ClientAttachmentDownloadStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(activeRuntime)
                    .ConfigureAwait(false);
            }

            lock (stateGate)
            {
                if (!ReferenceEquals(runtime, activeRuntime) ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready ||
                    !SelectionContainsAttachment(selection, attachmentId))
                {
                    return ClientAttachmentDownloadOutcome.Failure(
                        ClientAttachmentDownloadStatus.Canceled);
                }
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Downloading an attachment through the active account failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.LocalCacheFailure);
        }
    }

    public async Task<ClientAttachmentRevealOutcome> RevealAttachmentInFolderAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty)
        {
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.AttachmentUnavailable);
        }

        IClientAccountRuntime? activeRuntime;
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                !SelectionContainsAttachment(selection, attachmentId))
            {
                return ClientAttachmentRevealOutcome.FromStatus(
                    ClientAttachmentRevealStatus.AttachmentUnavailable);
            }

            activeRuntime = runtime;
        }

        if (activeRuntime is null)
        {
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.AttachmentUnavailable);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            selection.Token,
            lifetimeCancellation.Token);
        ClientAttachmentRevealStatus CommitReveal()
        {
            lock (stateGate)
            {
                if (!ReferenceEquals(runtime, activeRuntime) ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready ||
                    !SelectionContainsAttachment(selection, attachmentId))
                {
                    return ClientAttachmentRevealStatus.Stale;
                }

                if (linkedCancellation.IsCancellationRequested)
                {
                    return ClientAttachmentRevealStatus.Canceled;
                }

                // This one-way transition only authorizes Shell start. The runtime
                // releases this gate and its cache transaction before invoking the
                // potentially blocking native Shell operation.
                return ClientAttachmentRevealStatus.Revealed;
            }
        }

        try
        {
            var outcome = await activeRuntime
                .RevealAttachmentInFolderAsync(
                    selection.ConversationId,
                    attachmentId,
                    CommitReveal,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            lock (stateGate)
            {
                if (!ReferenceEquals(runtime, activeRuntime) ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready ||
                    !SelectionContainsAttachment(selection, attachmentId))
                {
                    return ClientAttachmentRevealOutcome.FromStatus(
                        ClientAttachmentRevealStatus.Stale);
                }
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Revealing an attachment through the active account failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.LocalCacheFailure);
        }
    }

    public async Task<ClientAttachmentOpenOutcome> OpenAttachmentAsync(
        Guid attachmentId,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty || ownerWindow == IntPtr.Zero)
        {
            return ClientAttachmentOpenOutcome.FromStatus(
                ClientAttachmentOpenStatus.AttachmentUnavailable);
        }

        IClientAccountRuntime? activeRuntime;
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                !SelectionContainsAttachment(selection, attachmentId))
            {
                return ClientAttachmentOpenOutcome.FromStatus(
                    ClientAttachmentOpenStatus.AttachmentUnavailable);
            }

            activeRuntime = runtime;
        }

        if (activeRuntime is null)
        {
            return ClientAttachmentOpenOutcome.FromStatus(
                ClientAttachmentOpenStatus.AttachmentUnavailable);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            selection.Token,
            lifetimeCancellation.Token);
        ClientAttachmentOpenStatus CommitOpen(Func<bool> commitPreparedJob)
        {
            ArgumentNullException.ThrowIfNull(commitPreparedJob);
            lock (stateGate)
            {
                if (!ReferenceEquals(runtime, activeRuntime) ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready ||
                    !SelectionContainsAttachment(selection, attachmentId))
                {
                    return ClientAttachmentOpenStatus.Stale;
                }

                if (linkedCancellation.IsCancellationRequested)
                {
                    return ClientAttachmentOpenStatus.Canceled;
                }

                // This runs only the coordinator's already prepared, synchronous
                // no-I/O state transition. The worker owns the later COM call.
                return commitPreparedJob()
                    ? ClientAttachmentOpenStatus.HandedToWindows
                    : ClientAttachmentOpenStatus.LocalFailure;
            }
        }

        try
        {
            return await activeRuntime
                .OpenAttachmentAsync(
                    selection.ConversationId,
                    attachmentId,
                    ownerWindow,
                    CommitOpen,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ClientAttachmentOpenOutcome.FromStatus(ClientAttachmentOpenStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentOpenOutcome.FromStatus(ClientAttachmentOpenStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Opening an attachment through the active account failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentOpenOutcome.FromStatus(ClientAttachmentOpenStatus.LocalFailure);
        }
    }

    public async Task<ClientAttachmentImageLoadOutcome> LoadAttachmentImageAsync(
        Guid attachmentId,
        ClientAttachmentImageRendition rendition,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty || !Enum.IsDefined(rendition))
        {
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.AttachmentUnavailable);
        }

        IClientAccountRuntime? activeRuntime;
        MessageSelection? selection;
        lock (stateGate)
        {
            selection = messageSelection;
            if (selection is null ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                !SelectionContainsImageAttachment(selection, attachmentId))
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    ClientAttachmentImageLoadStatus.AttachmentUnavailable);
            }

            activeRuntime = runtime;
        }

        if (activeRuntime is null)
        {
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.AttachmentUnavailable);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            selection.Token,
            lifetimeCancellation.Token);
        ClientAttachmentImageLoadStatus CommitImage()
        {
            lock (stateGate)
            {
                if (!ReferenceEquals(runtime, activeRuntime) ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready ||
                    !SelectionContainsImageAttachment(selection, attachmentId))
                {
                    return ClientAttachmentImageLoadStatus.Stale;
                }

                return linkedCancellation.IsCancellationRequested
                    ? ClientAttachmentImageLoadStatus.Canceled
                    : ClientAttachmentImageLoadStatus.Ready;
            }
        }

        try
        {
            var outcome = await activeRuntime
                .LoadAttachmentImageAsync(
                    selection.ConversationId,
                    attachmentId,
                    rendition,
                    CommitImage,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            lock (stateGate)
            {
                if (!ReferenceEquals(runtime, activeRuntime) ||
                    !IsCurrentMessageSelectionLocked(selection) ||
                    messageList.Status != ClientMessageListStatus.Ready ||
                    !SelectionContainsImageAttachment(selection, attachmentId))
                {
                    return ClientAttachmentImageLoadOutcome.Failure(
                        ClientAttachmentImageLoadStatus.Stale);
                }
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.Canceled);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(
                "Loading an attachment image through the active account failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.LocalCacheFailure);
        }
    }

    public async Task<ClientMessageSendOutcome> RetryPendingMessageAsync(
        Guid clientMessageId,
        CancellationToken cancellationToken = default)
    {
        if (clientMessageId == Guid.Empty)
        {
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed);
        }

        IClientAccountRuntime? activeRuntime;
        Guid conversationId;
        lock (stateGate)
        {
            if (messageSelection is not { } selection ||
                !IsCurrentMessageSelectionLocked(selection) ||
                messageList.Status != ClientMessageListStatus.Ready ||
                !messageList.Messages.Any(message =>
                    message.ClientMessageId == clientMessageId && message.CanRetry))
            {
                return ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.NotRetryable);
            }

            activeRuntime = runtime;
            conversationId = selection.ConversationId;
        }

        if (activeRuntime is null)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Unavailable);
        }

        try
        {
            var outcome = await activeRuntime
                .RetryPendingMessageAsync(
                    conversationId,
                    clientMessageId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Status == ClientMessageSendStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(activeRuntime)
                    .ConfigureAwait(false);
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Retrying a pending message through the active account failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.LocalCacheFailure);
        }
    }

    public void AcknowledgeMessageSnapshotApplied(
        Guid conversationId,
        long revision,
        long? observedThroughMessageId,
        bool isAtLatestRegion)
    {
        AcknowledgeMessageViewport(
            conversationId,
            revision,
            observedThroughMessageId,
            isAtLatestRegion,
            isSnapshotApplication: true);
    }

    public void AcknowledgeMessageViewportChanged(
        Guid conversationId,
        long revision,
        long? observedThroughMessageId,
        bool isAtLatestRegion)
    {
        AcknowledgeMessageViewport(
            conversationId,
            revision,
            observedThroughMessageId,
            isAtLatestRegion,
            isSnapshotApplication: false);
    }

    private void AcknowledgeMessageViewport(
        Guid conversationId,
        long revision,
        long? observedThroughMessageId,
        bool isAtLatestRegion,
        bool isSnapshotApplication)
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
                (!isSnapshotApplication && selection.AppliedRevision != revision) ||
                (observedThroughMessageId.HasValue &&
                 !selection.Messages.ContainsKey(observedThroughMessageId.Value)))
            {
                return;
            }

            if (isSnapshotApplication)
            {
                selection.AppliedRevision = revision;
            }
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
        SearchInvalidation searchInvalidation;
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
            searchInvalidation = InvalidateSearchResultsLocked();
            SnapshotChanged = null;
            ConversationListChanged = null;
            MessageListChanged = null;
            SearchResultsInvalidated = null;
        }

        lifetimeCancellation.Cancel();
        selection?.Cancel();
        subscription?.Detach();
        lease?.Dispose();
        CompleteSearchInvalidation(searchInvalidation);
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
                ClearActiveOwnership(out var lease, out var activeRuntime, out var searchInvalidation);
                CompleteSearchInvalidation(searchInvalidation);
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
                SearchResultsInvalidated = null;
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
        SearchInvalidation searchInvalidation;
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
            searchInvalidation = InvalidateSearchResultsLocked();
            activeDisplayName = null;
            activeServerBaseUri = null;
        }

        CompleteSearchInvalidation(searchInvalidation);
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

    private SearchInvalidation InvalidateSearchResultsLocked(
        bool publishHandlers = true,
        bool forcePublishHandlers = false)
    {
        var searchCancellation = searchFlightCancellation;
        var navigationCancellation = navigationFlightCancellation;
        var handlers = publishHandlers ? SearchResultsInvalidated : null;
        var shouldPublish = forcePublishHandlers || activeSearchResults.Count != 0 ||
            searchCancellation is not null || navigationCancellation is not null;
        ++searchSerial;
        ++navigationSerial;
        searchFlightCancellation = null;
        navigationFlightCancellation = null;
        activeSearchResults = Array.Empty<SearchResultDto>();
        activeSearchScope = null;
        return new SearchInvalidation(
            searchCancellation,
            navigationCancellation,
            shouldPublish ? handlers : null);
    }

    private SearchInvalidation InvalidateCurrentSearchResultsLocked()
    {
        if (activeSearchScope == ClientSearchScope.CurrentConversation)
        {
            return InvalidateSearchResultsLocked();
        }

        var navigationCancellation = navigationFlightCancellation;
        navigationFlightCancellation = null;
        ++navigationSerial;
        return new SearchInvalidation(
            SearchCancellation: null,
            NavigationCancellation: navigationCancellation,
            Handlers: null);
    }

    private bool IsCurrentSearchLeaseLocked(
        RuntimeSubscription subscription,
        MessageSelection? expectedSelection,
        ClientSearchScope scope,
        long serial,
        CancellationTokenSource flightCancellation) =>
        ReferenceEquals(runtimeSubscription, subscription) &&
        ReferenceEquals(runtime, subscription.Runtime) &&
        ReferenceEquals(searchFlightCancellation, flightCancellation) &&
        searchSerial == serial &&
        Volatile.Read(ref disposeStarted) == 0 &&
        (scope != ClientSearchScope.CurrentConversation ||
         (expectedSelection is not null &&
          IsCurrentMessageSelectionLocked(expectedSelection) &&
          messageList.Status == ClientMessageListStatus.Ready));

    private bool IsCurrentNavigationLeaseLocked(
        RuntimeSubscription subscription,
        SearchResultDto result,
        long expectedSearchSerial,
        long expectedNavigationSerial,
        CancellationTokenSource flightCancellation) =>
        ReferenceEquals(runtimeSubscription, subscription) &&
        ReferenceEquals(runtime, subscription.Runtime) &&
        ReferenceEquals(navigationFlightCancellation, flightCancellation) &&
        searchSerial == expectedSearchSerial &&
        navigationSerial == expectedNavigationSerial &&
        Volatile.Read(ref disposeStarted) == 0 &&
        ContainsActiveSearchResultLocked(result) &&
        IsConversationAuthorizedLocked(result.ConversationId);

    private bool AreSearchResultsAuthorizedLocked(IReadOnlyList<SearchResultDto> results) =>
        conversationList.Status == LocalCacheOperationStatus.Ready &&
        results.All(result => IsConversationAuthorizedLocked(result.ConversationId));

    private bool IsConversationAuthorizedLocked(Guid conversationId) =>
        conversationList.Status == LocalCacheOperationStatus.Ready &&
        conversationList.Conversations.Any(conversation => conversation.Id == conversationId);

    private bool ContainsActiveSearchResultLocked(SearchResultDto result) =>
        activeSearchResults.Any(candidate => ReferenceEquals(candidate, result));

    private static bool IsCompletedAroundForSearchResult(
        ClientMessageAroundOutcome around,
        SearchResultDto result) =>
        around.Status == ClientMessageLoadStatus.Completed &&
        around.TargetMessageId == result.MessageId &&
        around.Messages.Any(message =>
            message.Id == result.MessageId &&
            message.ConversationId == result.ConversationId);

    private static ClientSearchNavigationStatus MapSearchNavigationStatus(
        ClientMessageLoadStatus status) =>
        status switch
        {
            ClientMessageLoadStatus.Completed => ClientSearchNavigationStatus.Completed,
            ClientMessageLoadStatus.Canceled => ClientSearchNavigationStatus.Canceled,
            ClientMessageLoadStatus.AuthenticationRequired =>
                ClientSearchNavigationStatus.AuthenticationRequired,
            ClientMessageLoadStatus.AccessRevoked => ClientSearchNavigationStatus.AccessRevoked,
            ClientMessageLoadStatus.AccessDenied => ClientSearchNavigationStatus.AccessDenied,
            ClientMessageLoadStatus.TransientFailure =>
                ClientSearchNavigationStatus.TransientFailure,
            ClientMessageLoadStatus.ProtocolError => ClientSearchNavigationStatus.ProtocolError,
            ClientMessageLoadStatus.RemoteFailure => ClientSearchNavigationStatus.RemoteFailure,
            _ => ClientSearchNavigationStatus.LocalCacheFailure,
        };

    private static void CancelSearchFlight(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CompleteSearchInvalidation(SearchInvalidation invalidation)
    {
        CancelSearchFlight(invalidation.SearchCancellation);
        CancelSearchFlight(invalidation.NavigationCancellation);
        PublishSearchResultsInvalidated(invalidation.Handlers);
    }

    private void PublishSearchResultsInvalidated(Action? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing a search-results invalidation failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }
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
        SearchInvalidation invalidation;
        lock (stateGate)
        {
            if (!ReferenceEquals(runtimeSubscription, subscription) ||
                !ReferenceEquals(runtime, subscription.Runtime) ||
                Volatile.Read(ref disposeStarted) != 0)
            {
                return;
            }

            invalidation = InvalidateSearchResultsLocked();
        }

        CompleteSearchInvalidation(invalidation);
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
            if (!TryApplyLocalPage(
                    selection,
                    local,
                    replacePagingState: true,
                    replacePendingMessages: true))
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
                var around = selection.VerifiedAroundOutcome ?? await selection.Subscription.Runtime
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
            if (!TryApplyLocalPage(
                    selection,
                    local,
                    replacePagingState: false,
                    replacePendingMessages: false))
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
        bool replacePagingState,
        bool replacePendingMessages)
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
                var pageAttachmentIds = outcome.Messages
                    .SelectMany(static message => message.Attachments)
                    .Select(static attachment => attachment.Id)
                    .ToHashSet();
                if (outcome.DownloadedAttachmentIds.Any(
                        attachmentId => !pageAttachmentIds.Contains(attachmentId)))
                {
                    return false;
                }

                selection.DownloadedAttachmentIds.ExceptWith(pageAttachmentIds);
                selection.DownloadedAttachmentIds.UnionWith(
                    outcome.DownloadedAttachmentIds);
                if (replacePendingMessages && !selection.InitialUnreadStateCaptured)
                {
                    if (outcome.LastReadMessageId < 0 || outcome.UnreadCount < 0)
                    {
                        return false;
                    }

                    selection.InitialUnreadStateCaptured = true;
                    selection.InitialLastReadMessageId = outcome.LastReadMessageId;
                    selection.InitialUnreadCount = outcome.UnreadCount;
                    selection.NewMessageBoundaryResolved = outcome.UnreadCount == 0;
                }

                MergeMessagesLocked(selection, outcome.Messages);
                if (replacePendingMessages)
                {
                    selection.PendingMessages.Clear();
                    foreach (var pending in outcome.PendingMessages)
                    {
                        selection.PendingMessages[pending.ClientMessageId] = pending;
                    }
                }
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
                TryResolveNewMessageBoundaryFromHistoryLocked(selection, outcome);
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
                TryResolveNewMessageBoundaryFromAroundLocked(selection, outcome);
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

    private static bool SelectionContainsAttachment(
        MessageSelection selection,
        Guid attachmentId) =>
        selection.Messages.Values.Any(message =>
            message.Attachments.Any(attachment => attachment.Id == attachmentId));

    private static bool SelectionContainsImageAttachment(
        MessageSelection selection,
        Guid attachmentId) =>
        selection.Messages.Values.Any(message =>
            message.Attachments.Any(attachment =>
                attachment.Id == attachmentId &&
                attachment.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase)));

    private static void TryResolveNewMessageBoundaryFromHistoryLocked(
        MessageSelection selection,
        ClientMessageHistoryPageOutcome outcome)
    {
        if (!CanResolveNewMessageBoundary(selection) ||
            (outcome.HasMore &&
             (outcome.Messages.Count == 0 ||
              outcome.Messages[0].Id > selection.InitialLastReadMessageId)))
        {
            return;
        }

        ResolveNewMessageBoundaryLocked(selection);
    }

    private static void TryResolveNewMessageBoundaryFromAroundLocked(
        MessageSelection selection,
        ClientMessageAroundOutcome outcome)
    {
        if (!CanResolveNewMessageBoundary(selection) || outcome.Messages.Count == 0)
        {
            return;
        }

        var reachesReadBoundary = !outcome.HasMoreBefore ||
            outcome.Messages[0].Id <= selection.InitialLastReadMessageId;
        if (!reachesReadBoundary)
        {
            return;
        }

        var firstNewMessage = FindFirstNewMessageId(
            outcome.Messages,
            selection.InitialLastReadMessageId,
            selection.Subscription.Runtime.Identity.UserId);
        if (firstNewMessage.HasValue)
        {
            selection.NewMessageSeparatorBeforeMessageId = firstNewMessage;
            selection.NewMessageBoundaryResolved = true;
        }
        else if (!outcome.HasMoreAfter)
        {
            selection.NewMessageBoundaryResolved = true;
        }
    }

    private static bool CanResolveNewMessageBoundary(MessageSelection selection) =>
        selection.InitialUnreadStateCaptured &&
        selection.InitialUnreadCount > 0 &&
        !selection.NewMessageBoundaryResolved;

    private static void ResolveNewMessageBoundaryLocked(MessageSelection selection)
    {
        selection.NewMessageSeparatorBeforeMessageId = FindFirstNewMessageId(
            selection.Messages.Values,
            selection.InitialLastReadMessageId,
            selection.Subscription.Runtime.Identity.UserId);
        selection.NewMessageBoundaryResolved = true;
    }

    private static long? FindFirstNewMessageId(
        IEnumerable<MessageDto> messages,
        long lastReadMessageId,
        Guid currentUserId) =>
        messages
            .Where(message =>
                message.Id > lastReadMessageId &&
                message.SenderId != currentUserId)
            .Select(message => (long?)message.Id)
            .FirstOrDefault();

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
                if (!TryApplyLocalPage(
                        selection,
                        local,
                        replacePagingState: false,
                        replacePendingMessages: true))
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
        IClientAccountRuntime? activityRuntime = null;
        ClientActivitySnapshot? activity = null;
        lock (stateGate)
        {
            if (!IsCurrentMessageSelectionLocked(selection))
            {
                return false;
            }

            if (status != ClientMessageListStatus.Ready &&
                renderedConversationId == selection.ConversationId)
            {
                renderedConversationId = null;
                activityRuntime = runtime;
                activity = BuildRuntimeActivityLocked();
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
        if (activity is not null)
        {
            TryUpdateRuntimeActivity(activityRuntime, activity);
        }

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
        ClientMessageLoadStatus? lastLoadStatus)
    {
        var isReady = status == ClientMessageListStatus.Ready;
        return new ClientMessageListSnapshot(
            status,
            selection.ConversationId,
            isReady
                ? ClientMessageListPresenter.Present(
                    selection.Messages.Values,
                    selection.PendingMessages.Values,
                    selection.Subscription.Runtime.Identity.UserId,
                    selection.NewMessageBoundaryResolved
                        ? selection.NewMessageSeparatorBeforeMessageId
                        : null,
                    selection.DownloadedAttachmentIds)
                : Array.Empty<ClientMessageListItemPresentation>(),
            isLoading,
            isReady && selection.HasMoreBefore,
            isReady && selection.HasMoreAfter,
            isReady ? selection.TargetMessageId : null,
            lastLoadStatus);
    }

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
        out IClientAccountRuntime? activeRuntime,
        out SearchInvalidation searchInvalidation)
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
            searchInvalidation = InvalidateSearchResultsLocked();
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

    private sealed record SearchInvalidation(
        CancellationTokenSource? SearchCancellation,
        CancellationTokenSource? NavigationCancellation,
        Action? Handlers);

    private sealed class MessageSelection
    {
        private readonly CancellationTokenSource cancellation;
        private int canceled;

        public MessageSelection(
            Guid conversationId,
            long? targetMessageId,
            RuntimeSubscription subscription,
            CancellationTokenSource cancellation,
            ClientMessageAroundOutcome? verifiedAroundOutcome = null)
        {
            ConversationId = conversationId;
            TargetMessageId = targetMessageId;
            Subscription = subscription;
            this.cancellation = cancellation;
            Token = cancellation.Token;
            VerifiedAroundOutcome = verifiedAroundOutcome;
        }

        public Guid ConversationId { get; }

        public long? TargetMessageId { get; }

        public RuntimeSubscription Subscription { get; }

        public CancellationToken Token { get; }

        public ClientMessageAroundOutcome? VerifiedAroundOutcome { get; }

        public SortedDictionary<long, MessageDto> Messages { get; } = [];

        public Dictionary<Guid, LocalPendingMessage> PendingMessages { get; } = [];

        public HashSet<Guid> DownloadedAttachmentIds { get; } = [];

        public long? NextBeforeMessageId { get; set; }

        public bool HasMoreBefore { get; set; }

        public bool HasMoreAfter { get; set; }

        public long AppliedRevision { get; set; }

        public long? PendingObservedThroughMessageId { get; set; }

        public long CommittedObservedThroughMessageId { get; set; }

        public bool InitialUnreadStateCaptured { get; set; }

        public long InitialLastReadMessageId { get; set; }

        public int InitialUnreadCount { get; set; }

        public bool NewMessageBoundaryResolved { get; set; }

        public long? NewMessageSeparatorBeforeMessageId { get; set; }

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

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static string GetDeviceName() => Environment.MachineName;

    private static string GetClientVersion() =>
        typeof(ClientAccountShellCoordinator).Assembly.GetName().Version?.ToString() ??
        "1.0.0";
}
