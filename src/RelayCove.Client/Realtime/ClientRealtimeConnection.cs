using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Accounts;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Realtime;

public sealed class ClientRealtimeConnection : IClientAccountRealtimeConnection
{
    private const string HubPath = "hubs/chat";
    private const string NewMessageMethod = "NewMessage";
    private const string AccessGrantedMethod = "ConversationAccessGranted";
    private const string AccessRevokedMethod = "ConversationAccessRevoked";
    private const string AccountAccessRevokedMethod = "AccountAccessRevoked";
    private static readonly AsyncLocal<ClientRealtimeConnection?> CurrentDispatcher = new();

    private readonly HubConnection hubConnection;
    private readonly IRealtimeEventSink sink;
    private readonly ILogger<ClientRealtimeConnection> logger;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly Channel<RealtimeEvent> eventQueue = Channel.CreateUnbounded<RealtimeEvent>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource dispatchCancellation = new();
    private readonly IReadOnlyList<IDisposable> subscriptions;
    private readonly Task dispatchTask;
    private int state = (int)ConnectionState.Disconnected;
    private int stopRequested;
    private int disposed;

    public ClientRealtimeConnection(
        Uri serverBaseUri,
        Func<Task<string?>> accessTokenProvider,
        IRealtimeEventSink sink,
        ILogger<ClientRealtimeConnection> logger)
        : this(serverBaseUri, accessTokenProvider, sink, logger, configureHttp: null)
    {
    }

    internal ClientRealtimeConnection(
        Uri serverBaseUri,
        Func<Task<string?>> accessTokenProvider,
        IRealtimeEventSink sink,
        ILogger<ClientRealtimeConnection> logger,
        Action<HttpConnectionOptions>? configureHttp)
    {
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var hubUri = BuildHubUri(serverBaseUri);
        hubConnection = new HubConnectionBuilder()
            .WithUrl(
                hubUri,
                options =>
                {
                    options.AccessTokenProvider = accessTokenProvider;
                    configureHttp?.Invoke(options);
                })
            .WithAutomaticReconnect()
            .Build();

        subscriptions =
        [
            hubConnection.On<MessageDto>(NewMessageMethod, OnNewMessageAsync),
            hubConnection.On<Guid>(AccessGrantedMethod, OnAccessGrantedAsync),
            hubConnection.On<Guid>(AccessRevokedMethod, OnAccessRevokedAsync),
            hubConnection.On<AccountAccessRevokedEvent>(
                AccountAccessRevokedMethod,
                OnAccountAccessRevokedAsync),
        ];
        hubConnection.Reconnecting += OnReconnectingAsync;
        hubConnection.Reconnected += OnReconnectedAsync;
        hubConnection.Closed += OnClosedAsync;
        dispatchTask = Task.Run(ProcessEventsAsync);
    }

    public ConnectionState State => (ConnectionState)Volatile.Read(ref state);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Exception? failure = null;

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (hubConnection.State is not HubConnectionState.Disconnected)
            {
                return;
            }

            Volatile.Write(ref stopRequested, 0);
            _ = ChangeStateAsync(ConnectionState.Connecting);
            try
            {
                await hubConnection.StartAsync(cancellationToken);
                _ = ChangeStateAsync(ConnectionState.Connected);
            }
            catch (Exception exception)
            {
                failure = exception;
                _ = ChangeStateAsync(
                    cancellationToken.IsCancellationRequested
                        ? ConnectionState.Disconnected
                        : ConnectionState.ServerUnavailable,
                    exception);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(CurrentDispatcher.Value, this))
        {
            ScheduleDispatcherLifecycleAction(dispose: false);
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            Volatile.Write(ref stopRequested, 1);
            if (hubConnection.State is not HubConnectionState.Disconnected)
            {
                await hubConnection.StopAsync(cancellationToken);
            }

            _ = ChangeStateAsync(ConnectionState.Disconnected);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (ReferenceEquals(CurrentDispatcher.Value, this))
        {
            ScheduleDispatcherLifecycleAction(dispose: true);
            return;
        }

        Task? stateNotification = null;

        await lifecycleGate.WaitAsync();
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref stopRequested, 1);
            if (hubConnection.State is not HubConnectionState.Disconnected)
            {
                await hubConnection.StopAsync(CancellationToken.None);
            }

            stateNotification = ChangeStateAsync(ConnectionState.Disconnected);
            hubConnection.Reconnecting -= OnReconnectingAsync;
            hubConnection.Reconnected -= OnReconnectedAsync;
            hubConnection.Closed -= OnClosedAsync;
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            await hubConnection.DisposeAsync();
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (stateNotification is not null)
        {
            await stateNotification;
        }

        eventQueue.Writer.TryComplete();
        dispatchCancellation.Cancel();
        await CleanupDispatcherAsync();
    }

    private static Uri BuildHubUri(Uri serverBaseUri)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        var isHttpScheme = serverBaseUri.IsAbsoluteUri &&
            (string.Equals(serverBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(serverBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        if (!serverBaseUri.IsAbsoluteUri ||
            !isHttpScheme ||
            string.IsNullOrEmpty(serverBaseUri.Host) ||
            !string.IsNullOrEmpty(serverBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(serverBaseUri.Query) ||
            !string.IsNullOrEmpty(serverBaseUri.Fragment))
        {
            throw new ArgumentException(
                "Server base URI must be an absolute HTTP(S) URI without user info, query, or fragment.",
                nameof(serverBaseUri));
        }

        var builder = new UriBuilder(serverBaseUri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += '/';
        }

        return new Uri(builder.Uri, HubPath);
    }

    private Task OnNewMessageAsync(MessageDto message) =>
        EnqueueAsync(RealtimeEvent.ForMessage(message));

    private Task OnAccessGrantedAsync(Guid conversationId) =>
        EnqueueAsync(RealtimeEvent.ForAccessGranted(conversationId));

    private Task OnAccessRevokedAsync(Guid conversationId) =>
        EnqueueAsync(RealtimeEvent.ForAccessRevoked(conversationId));

    private Task OnAccountAccessRevokedAsync(AccountAccessRevokedEvent accountAccessRevoked) =>
        EnqueueAsync(RealtimeEvent.ForAccountAccessRevoked(accountAccessRevoked));

    private Task OnReconnectingAsync(Exception? exception) =>
        ChangeStateAsync(ConnectionState.Reconnecting, exception);

    private Task OnReconnectedAsync(string? connectionId) =>
        ChangeStateAsync(ConnectionState.Connected);

    private Task OnClosedAsync(Exception? exception) =>
        ChangeStateAsync(
            Volatile.Read(ref stopRequested) == 1
                ? ConnectionState.Disconnected
                : ConnectionState.ServerUnavailable,
            exception);

    private Task ChangeStateAsync(ConnectionState nextState, Exception? exception = null)
    {
        var previousState = (ConnectionState)Interlocked.Exchange(ref state, (int)nextState);
        if (previousState == nextState)
        {
            return Task.CompletedTask;
        }

        if (exception is null)
        {
            logger.LogInformation(
                "Realtime connection state changed from {PreviousState} to {NextState}.",
                previousState,
                nextState);
        }
        else
        {
            logger.LogWarning(
                "Realtime connection state changed from {PreviousState} to {NextState}; errorType={ErrorType}.",
                previousState,
                nextState,
                exception.GetType().FullName);
        }

        return EnqueueAsync(RealtimeEvent.ForState(nextState));
    }

    private Task EnqueueAsync(RealtimeEvent realtimeEvent)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedEvent = realtimeEvent with { Completion = completion };
        if (!eventQueue.Writer.TryWrite(queuedEvent))
        {
            completion.TrySetResult();
        }

        return completion.Task;
    }

    private async Task ProcessEventsAsync()
    {
        await foreach (var realtimeEvent in eventQueue.Reader.ReadAllAsync(dispatchCancellation.Token))
        {
            var previousDispatcher = CurrentDispatcher.Value;
            CurrentDispatcher.Value = this;
            try
            {
                switch (realtimeEvent.Kind)
                {
                    case RealtimeEventKind.ConnectionState:
                        await sink.OnConnectionStateChangedAsync(
                            realtimeEvent.ConnectionState,
                            dispatchCancellation.Token);
                        break;
                    case RealtimeEventKind.NewMessage:
                        await sink.OnNewMessageAsync(
                            realtimeEvent.Message!,
                            dispatchCancellation.Token);
                        break;
                    case RealtimeEventKind.ConversationAccessGranted:
                        await sink.OnConversationAccessGrantedAsync(
                            realtimeEvent.ConversationId,
                            dispatchCancellation.Token);
                        break;
                    case RealtimeEventKind.ConversationAccessRevoked:
                        await sink.OnConversationAccessRevokedAsync(
                            realtimeEvent.ConversationId,
                            dispatchCancellation.Token);
                        break;
                    case RealtimeEventKind.AccountAccessRevoked:
                        await sink.OnAccountAccessRevokedAsync(
                            realtimeEvent.AccountAccessRevoked!,
                            dispatchCancellation.Token);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown realtime event kind.");
                }
            }
            catch (OperationCanceledException) when (dispatchCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogSinkFailure(realtimeEvent, exception);
            }
            finally
            {
                CurrentDispatcher.Value = previousDispatcher;
                realtimeEvent.Completion.TrySetResult();
            }
        }
    }

    private void LogSinkFailure(RealtimeEvent realtimeEvent, Exception exception)
    {
        logger.LogWarning(
            "Realtime sink failed; kind={EventKind}; messageId={MessageId}; conversationId={ConversationId}; state={ConnectionState}; errorType={ErrorType}.",
            realtimeEvent.Kind,
            realtimeEvent.Message?.Id,
            realtimeEvent.Message?.ConversationId ?? realtimeEvent.ConversationId,
            realtimeEvent.Kind == RealtimeEventKind.ConnectionState
                ? realtimeEvent.ConnectionState
                : null,
            exception.GetType().FullName);
    }

    private async Task CleanupDispatcherAsync()
    {
        try
        {
            await dispatchTask;
        }
        catch (OperationCanceledException)
        {
        }

        dispatchCancellation.Dispose();
        lifecycleGate.Dispose();
    }

    private void ScheduleDispatcherLifecycleAction(bool dispose)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (dispose)
                    {
                        await DisposeAsync();
                    }
                    else
                    {
                        await StopAsync(CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Deferred realtime lifecycle action failed; dispose={Dispose}; errorType={ErrorType}.",
                        dispose,
                        exception.GetType().FullName);
                }
            });
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private enum RealtimeEventKind
    {
        ConnectionState,
        NewMessage,
        ConversationAccessGranted,
        ConversationAccessRevoked,
        AccountAccessRevoked,
    }

    private sealed record RealtimeEvent(
        RealtimeEventKind Kind,
        ConnectionState ConnectionState,
        MessageDto? Message,
        Guid ConversationId,
        AccountAccessRevokedEvent? AccountAccessRevoked,
        TaskCompletionSource Completion)
    {
        public static RealtimeEvent ForState(ConnectionState state) =>
            new(
                RealtimeEventKind.ConnectionState,
                state,
                Message: null,
                ConversationId: Guid.Empty,
                AccountAccessRevoked: null,
                Completion: null!);

        public static RealtimeEvent ForMessage(MessageDto message) =>
            new(
                RealtimeEventKind.NewMessage,
                ConnectionState.Disconnected,
                message,
                message.ConversationId,
                AccountAccessRevoked: null,
                Completion: null!);

        public static RealtimeEvent ForAccessGranted(Guid conversationId) =>
            new(
                RealtimeEventKind.ConversationAccessGranted,
                ConnectionState.Disconnected,
                Message: null,
                conversationId,
                AccountAccessRevoked: null,
                Completion: null!);

        public static RealtimeEvent ForAccessRevoked(Guid conversationId) =>
            new(
                RealtimeEventKind.ConversationAccessRevoked,
                ConnectionState.Disconnected,
                Message: null,
                conversationId,
                AccountAccessRevoked: null,
                Completion: null!);

        public static RealtimeEvent ForAccountAccessRevoked(
            AccountAccessRevokedEvent accountAccessRevoked) =>
            new(
                RealtimeEventKind.AccountAccessRevoked,
                ConnectionState.Disconnected,
                Message: null,
                ConversationId: Guid.Empty,
                AccountAccessRevoked: accountAccessRevoked,
                Completion: null!);
    }
}
