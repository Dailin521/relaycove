using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RelayCove.Client.Realtime;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Tests.Realtime;

public sealed class ClientRealtimeConnectionTests
{
    private const string ValidToken = "valid-test-token";

    [Fact]
    public async Task Events_WhenAuthenticated_AreCompleteAndSerializedAcrossConcurrentLifecycleCalls()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var sink = new BlockingRevocationSink();
        var tokenCalls = 0;
        await using var connection = CreateConnection(
            host,
            () =>
            {
                Interlocked.Increment(ref tokenCalls);
                return Task.FromResult<string?>(ValidToken);
            },
            sink);

        await Task.WhenAll(connection.StartAsync(), connection.StartAsync());
        Assert.Equal(ConnectionState.Connected, connection.State);
        await WaitUntilAsync(() => sink.States.Contains(ConnectionState.Connected));

        var attachmentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var firstMessage = CreateMessage(id: 101, content: "complete realtime payload") with
        {
            Type = MessageType.Image,
            Content = null,
            Attachments =
            [
                new AttachmentDto(
                    attachmentId,
                    "realtime-image.png",
                    "image/png",
                    1024,
                    $"/api/attachments/{attachmentId:D}/download",
                    ThumbnailUrl: null),
            ],
        };
        await host.HubContext.Clients.All.SendAsync("NewMessage", firstMessage);
        var receivedMessage = await sink.FirstMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertMessageEqual(firstMessage, receivedMessage);

        await host.HubContext.Clients.All.SendAsync(
            "ConversationAccessRevoked",
            firstMessage.ConversationId);
        await sink.RevocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var lateMessage = CreateMessage(
            id: 102,
            content: "must wait behind revocation",
            conversationId: firstMessage.ConversationId);
        await host.HubContext.Clients.All.SendAsync("NewMessage", lateMessage);
        await Task.Delay(150);
        Assert.DoesNotContain(sink.Messages, message => message.Id == lateMessage.Id);

        sink.ReleaseRevocation.TrySetResult();
        await sink.LateMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            ["revocation-start", "revocation-end", "late-message"],
            sink.OrderedBarrierEvents);
        Assert.Equal(firstMessage.ConversationId, sink.RevokedConversationId);
        Assert.True(Volatile.Read(ref tokenCalls) >= 1);
        Assert.NotEmpty(host.TokenRecorder.AuthorizationHeaders);
        Assert.All(
            host.TokenRecorder.AuthorizationHeaders,
            header => Assert.Equal($"Bearer {ValidToken}", header));

        await Task.WhenAll(connection.StopAsync(), connection.StopAsync());
        Assert.Equal(ConnectionState.Disconnected, connection.State);
        await WaitUntilAsync(() => sink.States.Contains(ConnectionState.Disconnected));
        Assert.Equal(
            [
                ConnectionState.Connecting,
                ConnectionState.Connected,
                ConnectionState.Disconnected,
            ],
            sink.States);
    }

    [Fact]
    public async Task StartAsync_WhenUnauthorized_SurfacesFailureWithoutLoggingTokenOrPayload()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var sink = new RecordingSink();
        var logger = new RecordingLogger<ClientRealtimeConnection>();
        const string invalidToken = "invalid-secret-token";
        var currentToken = invalidToken;
        await using var connection = CreateConnection(
            host,
            () => Task.FromResult<string?>(Volatile.Read(ref currentToken)),
            sink,
            logger);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Equal(ConnectionState.ServerUnavailable, connection.State);
        await WaitUntilAsync(() => sink.States.Count >= 2);
        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.ServerUnavailable],
            sink.States);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(invalidToken, StringComparison.Ordinal));

        Volatile.Write(ref currentToken, ValidToken);
        await connection.StartAsync();
        Assert.Equal(ConnectionState.Connected, connection.State);
        await WaitUntilAsync(() => sink.States.Count >= 4);
        Assert.Equal(
            [
                ConnectionState.Connecting,
                ConnectionState.ServerUnavailable,
                ConnectionState.Connecting,
                ConnectionState.Connected,
            ],
            sink.States);
        await connection.StopAsync();
    }

    [Fact]
    public async Task EventSink_WhenOneCallbackFails_ContinuesWithoutLoggingSensitivePayload()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var sink = new ThrowingMessageSink();
        var logger = new RecordingLogger<ClientRealtimeConnection>();
        await using var connection = CreateConnection(
            host,
            () => Task.FromResult<string?>(ValidToken),
            sink,
            logger);
        await connection.StartAsync();
        var message = CreateMessage(
            id: 201,
            content: "sensitive-body-that-must-not-be-logged");

        await host.HubContext.Clients.All.SendAsync("NewMessage", message);
        await sink.MessageAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.HubContext.Clients.All.SendAsync(
            "ConversationAccessRevoked",
            message.ConversationId);
        await sink.RevocationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            logger.Messages,
            log =>
                log.Contains("kind=NewMessage", StringComparison.Ordinal) &&
                log.Contains("messageId=201", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            log =>
                log.Contains(message.Content!, StringComparison.Ordinal) ||
                log.Contains(message.SenderDisplayName, StringComparison.Ordinal) ||
                log.Contains(ValidToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connection_WhenEstablishedTransportDrops_ReportsReconnectingThenConnected()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var sink = new RecordingSink();
        var logger = new RecordingLogger<ClientRealtimeConnection>();
        await using var connection = CreateConnection(
            host,
            () => Task.FromResult<string?>(ValidToken),
            sink,
            logger);

        await connection.StartAsync();
        await host.Control.PollRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Control.AbortActivePoll();
        await WaitUntilAsync(() =>
            sink.States.Count(state => state == ConnectionState.Connected) >= 2 &&
            sink.States.Contains(ConnectionState.Reconnecting));

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal(
            [
                ConnectionState.Connecting,
                ConnectionState.Connected,
                ConnectionState.Reconnecting,
                ConnectionState.Connected,
            ],
            sink.States.Take(4));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://relaycove.example/")]
    [InlineData("https://user:password@relaycove.example/")]
    [InlineData("https://relaycove.example/?token=secret")]
    [InlineData("https://relaycove.example/#fragment")]
    public void Constructor_WhenServerBaseUriIsUnsafe_RejectsIt(string serverBaseUri)
    {
        var sink = new RecordingSink();

        Assert.Throws<ArgumentException>(() => new ClientRealtimeConnection(
            new Uri(serverBaseUri, UriKind.RelativeOrAbsolute),
            () => Task.FromResult<string?>(ValidToken),
            sink,
            NullLogger<ClientRealtimeConnection>.Instance));
    }

    [Fact]
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var connection = CreateConnection(
            host,
            () => Task.FromResult<string?>(ValidToken),
            new RecordingSink());
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.StartAsync());
    }

    [Fact]
    public async Task StateSink_WhenItStopsConnectedConnection_DoesNotDeadlockLifecycle()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var sink = new StopOnConnectedSink();
        await using var connection = CreateConnection(
            host,
            () => Task.FromResult<string?>(ValidToken),
            sink);
        sink.Connection = connection;

        await connection.StartAsync();
        await sink.StopCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => sink.States.Contains(ConnectionState.Disconnected));

        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task MessageSink_WhenItStopsConnection_DoesNotDeadlockHubCallback()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var sink = new StopOnMessageSink();
        await using var connection = CreateConnection(
            host,
            () => Task.FromResult<string?>(ValidToken),
            sink);
        sink.Connection = connection;
        await connection.StartAsync();

        await host.HubContext.Clients.All.SendAsync(
            "NewMessage",
            CreateMessage(id: 301, content: "stop from serialized sink"));
        await sink.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => connection.State == ConnectionState.Disconnected);
        await WaitUntilAsync(() => sink.States.Contains(ConnectionState.Disconnected));

        Assert.Contains(ConnectionState.Disconnected, sink.States);
    }

    [Fact]
    public async Task MessageSink_WhenItDisposesConnection_DoesNotDeadlockHubCallback()
    {
        await using var host = await RealtimeTestHost.StartAsync();
        var sink = new DisposeOnMessageSink();
        var connection = CreateConnection(
            host,
            () => Task.FromResult<string?>(ValidToken),
            sink);
        sink.Connection = connection;
        await connection.StartAsync();

        await host.HubContext.Clients.All.SendAsync(
            "NewMessage",
            CreateMessage(id: 302, content: "dispose from serialized sink"));
        await sink.DisposeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => connection.State == ConnectionState.Disconnected);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.StartAsync());
        await connection.DisposeAsync();
    }

    private static ClientRealtimeConnection CreateConnection(
        RealtimeTestHost host,
        Func<Task<string?>> accessTokenProvider,
        IRealtimeEventSink sink,
        ILogger<ClientRealtimeConnection>? logger = null) =>
        new(
            new Uri("http://localhost/relay/"),
            accessTokenProvider,
            sink,
            logger ?? NullLogger<ClientRealtimeConnection>.Instance,
            options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ =>
                    new AbortableTestHandler(host.Server.CreateHandler(), host.Control);
            });

    private static MessageDto CreateMessage(
        long id,
        string content,
        Guid? conversationId = null) =>
        new(
            id,
            Guid.NewGuid(),
            conversationId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            "sensitive-display-name",
            MessageType.Text,
            content,
            ReplyToMessageId: null,
            Attachments: [],
            MentionUserIds: [Guid.NewGuid()],
            DateTimeOffset.UtcNow);

    private static void AssertMessageEqual(MessageDto expected, MessageDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ClientMessageId, actual.ClientMessageId);
        Assert.Equal(expected.ConversationId, actual.ConversationId);
        Assert.Equal(expected.SenderId, actual.SenderId);
        Assert.Equal(expected.SenderDisplayName, actual.SenderDisplayName);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(expected.ReplyToMessageId, actual.ReplyToMessageId);
        Assert.Equal(expected.Attachments, actual.Attachments);
        Assert.Equal(expected.MentionUserIds, actual.MentionUserIds);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class BlockingRevocationSink : RecordingSink
    {
        public TaskCompletionSource<MessageDto> FirstMessage { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<MessageDto> LateMessage { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RevocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRevocation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> OrderedBarrierEvents { get; } = [];

        public Guid RevokedConversationId { get; private set; }

        public override Task OnNewMessageAsync(
            MessageDto message,
            CancellationToken cancellationToken)
        {
            Messages.Enqueue(message);
            if (!FirstMessage.TrySetResult(message))
            {
                OrderedBarrierEvents.Enqueue("late-message");
                LateMessage.TrySetResult(message);
            }

            return Task.CompletedTask;
        }

        public override async Task OnConversationAccessRevokedAsync(
            Guid conversationId,
            CancellationToken cancellationToken)
        {
            RevokedConversationId = conversationId;
            OrderedBarrierEvents.Enqueue("revocation-start");
            RevocationStarted.TrySetResult();
            await ReleaseRevocation.Task.WaitAsync(cancellationToken);
            OrderedBarrierEvents.Enqueue("revocation-end");
        }
    }

    private class RecordingSink : IRealtimeEventSink
    {
        public ConcurrentQueue<ConnectionState> States { get; } = [];

        public ConcurrentQueue<MessageDto> Messages { get; } = [];

        public virtual Task OnConnectionStateChangedAsync(
            ConnectionState state,
            CancellationToken cancellationToken)
        {
            States.Enqueue(state);
            return Task.CompletedTask;
        }

        public virtual Task OnNewMessageAsync(
            MessageDto message,
            CancellationToken cancellationToken)
        {
            Messages.Enqueue(message);
            return Task.CompletedTask;
        }

        public virtual Task OnConversationAccessRevokedAsync(
            Guid conversationId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingMessageSink : RecordingSink
    {
        public TaskCompletionSource MessageAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RevocationReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task OnNewMessageAsync(
            MessageDto message,
            CancellationToken cancellationToken)
        {
            MessageAttempted.TrySetResult();
            throw new InvalidOperationException("Synthetic sink failure.");
        }

        public override Task OnConversationAccessRevokedAsync(
            Guid conversationId,
            CancellationToken cancellationToken)
        {
            RevocationReceived.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class StopOnConnectedSink : RecordingSink
    {
        public ClientRealtimeConnection? Connection { get; set; }

        public TaskCompletionSource StopCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task OnConnectionStateChangedAsync(
            ConnectionState state,
            CancellationToken cancellationToken)
        {
            await base.OnConnectionStateChangedAsync(state, cancellationToken);
            if (state == ConnectionState.Connected && Connection is not null)
            {
                await Connection.StopAsync(cancellationToken);
                StopCompleted.TrySetResult();
            }
        }
    }

    private sealed class StopOnMessageSink : RecordingSink
    {
        public ClientRealtimeConnection? Connection { get; set; }

        public TaskCompletionSource StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task OnNewMessageAsync(
            MessageDto message,
            CancellationToken cancellationToken)
        {
            await base.OnNewMessageAsync(message, cancellationToken);
            if (Connection is not null)
            {
                await Connection.StopAsync(cancellationToken);
            }

            StopRequested.TrySetResult();
        }
    }

    private sealed class DisposeOnMessageSink : RecordingSink
    {
        public ClientRealtimeConnection? Connection { get; set; }

        public TaskCompletionSource DisposeRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task OnNewMessageAsync(
            MessageDto message,
            CancellationToken cancellationToken)
        {
            await base.OnNewMessageAsync(message, cancellationToken);
            if (Connection is not null)
            {
                await Connection.DisposeAsync();
            }

            DisposeRequested.TrySetResult();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class RealtimeTestHost(
        WebApplication application,
        TestServer server,
        IHubContext<RealtimeTestHub> hubContext,
        TestTokenRecorder tokenRecorder,
        TestHubControl control) : IAsyncDisposable
    {
        public TestServer Server { get; } = server;

        public IHubContext<RealtimeTestHub> HubContext { get; } = hubContext;

        public TestTokenRecorder TokenRecorder { get; } = tokenRecorder;

        public TestHubControl Control { get; } = control;

        public static async Task<RealtimeTestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ClientRealtimeConnectionTests).Assembly.FullName,
                EnvironmentName = "Development",
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TestTokenRecorder>();
            builder.Services.AddSingleton<TestHubControl>();
            builder.Services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSignalR();

            var application = builder.Build();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapHub<RealtimeTestHub>("/relay/hubs/chat").RequireAuthorization();
            await application.StartAsync();

            return new RealtimeTestHost(
                application,
                application.GetTestServer(),
                application.Services.GetRequiredService<IHubContext<RealtimeTestHub>>(),
                application.Services.GetRequiredService<TestTokenRecorder>(),
                application.Services.GetRequiredService<TestHubControl>());
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
        }
    }

    private sealed class RealtimeTestHub : Hub;

    private sealed class TestHubControl
    {
        private CancellationTokenSource? activePoll;

        public TaskCompletionSource PollRegistered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RegisterPoll(CancellationTokenSource pollCancellation)
        {
            Volatile.Write(ref activePoll, pollCancellation);
            PollRegistered.TrySetResult();
        }

        public void AbortActivePoll()
        {
            var pollCancellation = Volatile.Read(ref activePoll) ??
                throw new InvalidOperationException("No active test poll is registered.");
            pollCancellation.Cancel();
        }
    }

    private sealed class AbortableTestHandler(
        HttpMessageHandler innerHandler,
        TestHubControl control) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            control.RegisterPoll(pollCancellation);
            try
            {
                return await base.SendAsync(request, pollCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("Synthetic long-poll transport failure.");
            }
        }
    }

    private sealed class TestTokenRecorder
    {
        public ConcurrentQueue<string> AuthorizationHeaders { get; } = [];
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestTokenRecorder tokenRecorder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "RealtimeTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var authorizationHeader = Request.Headers.Authorization.ToString();
            tokenRecorder.AuthorizationHeaders.Enqueue(authorizationHeader);
            if (!string.Equals(
                    authorizationHeader,
                    $"Bearer {ValidToken}",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid test token."));
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001")],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
