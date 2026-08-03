using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayCove.Server.Data;
using RelayCove.Server.Hubs;
using RelayCove.Server.Realtime;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Realtime;

public sealed class SignalRRealtimeTests : IClassFixture<RelayCoveWebApplicationFactory>
{
    private const string ExistingPassword = "Correct horse battery staple 123!";
    private static long syntheticMessageId = 10_000_000;
    private readonly RelayCoveWebApplicationFactory factory;

    public SignalRRealtimeTests(RelayCoveWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ChatHub_WhenAuthenticating_RequiresActiveJwtAndLimitsQueryTokenToHubPath()
    {
        var activeName = CreateUserName("signalr-auth-active");
        var disabledName = CreateUserName("signalr-auth-disabled");
        await factory.CreateUserAsync(activeName, ExistingPassword);
        var disabledId = await factory.CreateUserAsync(disabledName, ExistingPassword);
        var activeLogin = await LoginAsync(factory.CreateClient(), activeName);
        var disabledLogin = await LoginAsync(factory.CreateClient(), disabledName);
        await factory.SetUserDisabledAsync(disabledId, isDisabled: true);

        await using (var missingTokenConnection = CreateHubConnection(factory, accessToken: null))
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(
                () => missingTokenConnection.StartAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        }

        await using (var disabledConnection = CreateHubConnection(factory, disabledLogin.AccessToken))
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(
                () => disabledConnection.StartAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        }

        using var client = factory.CreateClient();
        using (var nonHubResponse = await client.GetAsync(
                   $"/api/auth/me?access_token={Uri.EscapeDataString(activeLogin.AccessToken)}"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, nonHubResponse.StatusCode);
        }

        var logOffset = factory.LogMessages.Count;
        using (var hubQueryResponse = await client.GetAsync(
                   $"{ChatHub.Route}?access_token={Uri.EscapeDataString(activeLogin.AccessToken)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, hubQueryResponse.StatusCode);
        }

        using (var ambiguousQueryResponse = await client.GetAsync(
                   $"{ChatHub.Route}?access_token={Uri.EscapeDataString(activeLogin.AccessToken)}" +
                   $"&access_token={Uri.EscapeDataString(activeLogin.AccessToken)}"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ambiguousQueryResponse.StatusCode);
        }

        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message => message.Contains(activeLogin.AccessToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatHub_WhenConnectionStarts_JoinsOnlyCurrentVisibleConversationGroups()
    {
        var adminName = CreateUserName("signalr-groups-admin");
        var memberName = CreateUserName("signalr-groups-member");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        var memberLogin = await LoginAsync(factory.CreateClient(), memberName);
        var publicConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            "SignalR public groups");
        var privateConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "SignalR private groups");
        await UpsertMemberAsync(adminClient, privateConversation.Id, memberId);
        var inaccessibleConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "SignalR hidden groups");
        var directConversation = await CreateDirectAsync(adminClient, memberId);
        var received = new ConcurrentQueue<MessageDto>();

        await using var connection = CreateHubConnection(factory, memberLogin.AccessToken);
        connection.On<MessageDto>(nameof(IChatClient.NewMessage), received.Enqueue);
        var initialConnectionLogOffset = factory.LogMessages.Count;
        await connection.StartAsync();
        await WaitForGroupJoinAsync(initialConnectionLogOffset, memberId);

        var publicProbe = CreateSyntheticMessage(publicConversation.Id, memberId, "public group");
        var privateProbe = CreateSyntheticMessage(privateConversation.Id, memberId, "private group");
        var directProbe = CreateSyntheticMessage(directConversation.Id, memberId, "direct group");
        var hiddenProbe = CreateSyntheticMessage(inaccessibleConversation.Id, memberId, "hidden group");
        var initialBarrier = CreateSyntheticMessage(publicConversation.Id, memberId, "initial barrier");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub, IChatClient>>();
            await hubContext.Clients.Group(ConversationHubGroup.For(publicConversation.Id))
                .NewMessage(publicProbe);
            await hubContext.Clients.Group(ConversationHubGroup.For(privateConversation.Id))
                .NewMessage(privateProbe);
            await hubContext.Clients.Group(ConversationHubGroup.For(directConversation.Id))
                .NewMessage(directProbe);
            await hubContext.Clients.Group(ConversationHubGroup.For(inaccessibleConversation.Id))
                .NewMessage(hiddenProbe);
            await hubContext.Clients.User(memberId.ToString("D")).NewMessage(initialBarrier);
        }

        await WaitUntilAsync(() => received.Any(message => message.Id == initialBarrier.Id));
        Assert.Equal(
            new[] { publicProbe.Id, privateProbe.Id, directProbe.Id, initialBarrier.Id }.Order(),
            received.Select(message => message.Id).Order());
        Assert.DoesNotContain(received, message => message.Id == hiddenProbe.Id);

        var joinedAfterConnection = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "SignalR reconnect groups");
        await UpsertMemberAsync(adminClient, joinedAfterConnection.Id, memberId);
        var beforeReconnectProbe = CreateSyntheticMessage(
            joinedAfterConnection.Id,
            memberId,
            "before reconnect");
        var reconnectBarrier = CreateSyntheticMessage(publicConversation.Id, memberId, "reconnect barrier");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub, IChatClient>>();
            await hubContext.Clients.Group(ConversationHubGroup.For(joinedAfterConnection.Id))
                .NewMessage(beforeReconnectProbe);
            await hubContext.Clients.User(memberId.ToString("D")).NewMessage(reconnectBarrier);
        }

        await WaitUntilAsync(() => received.Any(message => message.Id == reconnectBarrier.Id));
        Assert.DoesNotContain(received, message => message.Id == beforeReconnectProbe.Id);
        await connection.StopAsync();
        var reconnectLogOffset = factory.LogMessages.Count;
        await connection.StartAsync();
        await WaitForGroupJoinAsync(reconnectLogOffset, memberId);
        var afterReconnectProbe = CreateSyntheticMessage(
            joinedAfterConnection.Id,
            memberId,
            "after reconnect");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub, IChatClient>>();
            await hubContext.Clients.Group(ConversationHubGroup.For(joinedAfterConnection.Id))
                .NewMessage(afterReconnectProbe);
        }

        await WaitUntilAsync(() => received.Any(message => message.Id == afterReconnectProbe.Id));
    }

    [Fact]
    public async Task MessageSend_WhenCreated_PublishesOnceToCurrentAuthorizedUsers()
    {
        var adminName = CreateUserName("signalr-send-admin");
        var memberName = CreateUserName("signalr-send-member");
        var outsiderName = CreateUserName("signalr-send-outsider");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        await factory.CreateUserAsync(outsiderName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        var memberLogin = await LoginAsync(factory.CreateClient(), memberName);
        var adminLogin = await LoginAsync(factory.CreateClient(), adminName);
        var outsiderLogin = await LoginAsync(factory.CreateClient(), outsiderName);
        var conversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "SignalR delivery");
        await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        var adminMessages = new ConcurrentQueue<MessageDto>();
        var memberMessages = new ConcurrentQueue<MessageDto>();
        var outsiderMessages = new ConcurrentQueue<MessageDto>();
        await using var adminConnection = CreateHubConnection(factory, adminLogin.AccessToken);
        await using var memberConnection = CreateHubConnection(factory, memberLogin.AccessToken);
        await using var outsiderConnection = CreateHubConnection(factory, outsiderLogin.AccessToken);
        adminConnection.On<MessageDto>(nameof(IChatClient.NewMessage), adminMessages.Enqueue);
        memberConnection.On<MessageDto>(nameof(IChatClient.NewMessage), memberMessages.Enqueue);
        outsiderConnection.On<MessageDto>(nameof(IChatClient.NewMessage), outsiderMessages.Enqueue);
        await Task.WhenAll(
            adminConnection.StartAsync(),
            memberConnection.StartAsync(),
            outsiderConnection.StartAsync());
        const string secretContent = "signalr exact secret content 8d0d";
        var request = CreateSendRequest(conversation.Id, secretContent) with
        {
            MentionUserIds = [memberId],
        };
        var logOffset = factory.LogMessages.Count;

        using (var createdResponse = await adminClient.PostAsJsonAsync("/api/messages", request))
        {
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        }

        await WaitUntilAsync(() => adminMessages.Count == 1 && memberMessages.Count == 1);
        Assert.Equal(secretContent, adminMessages.Single().Content);
        Assert.Equal(secretContent, memberMessages.Single().Content);
        Assert.Equal([memberId], adminMessages.Single().MentionUserIds);
        Assert.Empty(outsiderMessages);

        using (var replayResponse = await adminClient.PostAsJsonAsync("/api/messages", request))
        {
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        }

        await Task.Delay(200);
        Assert.Single(adminMessages);
        Assert.Single(memberMessages);
        Assert.Empty(outsiderMessages);

        var concurrentRequest = CreateSendRequest(conversation.Id, "concurrent realtime winner");
        var firstConcurrentTask = adminClient.PostAsJsonAsync("/api/messages", concurrentRequest);
        var secondConcurrentTask = adminClient.PostAsJsonAsync("/api/messages", concurrentRequest);
        using var firstConcurrentResponse = await firstConcurrentTask;
        using var secondConcurrentResponse = await secondConcurrentTask;
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Created],
            new[] { firstConcurrentResponse.StatusCode, secondConcurrentResponse.StatusCode }.Order());
        await WaitUntilAsync(() => adminMessages.Count == 2 && memberMessages.Count == 2);
        await Task.Delay(200);
        Assert.Equal(2, adminMessages.Count);
        Assert.Equal(2, memberMessages.Count);
        Assert.Empty(outsiderMessages);

        using (var removeResponse = await adminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);
        }

        using (var nextResponse = await adminClient.PostAsJsonAsync(
                   "/api/messages",
                   CreateSendRequest(conversation.Id, "after current revocation")))
        {
            Assert.Equal(HttpStatusCode.Created, nextResponse.StatusCode);
        }

        await WaitUntilAsync(() => adminMessages.Count == 3);
        await Task.Delay(200);
        Assert.Equal(2, memberMessages.Count);
        Assert.Empty(outsiderMessages);
        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message => message.Contains(secretContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewMessagePublisher_WhenConversationTypesVary_UsesCurrentActiveRecipientSnapshot()
    {
        var adminName = CreateUserName("signalr-recipients-admin");
        var memberName = CreateUserName("signalr-recipients-member");
        var outsiderName = CreateUserName("signalr-recipients-outsider");
        var disabledName = CreateUserName("signalr-recipients-disabled");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        var outsiderId = await factory.CreateUserAsync(outsiderName, ExistingPassword);
        var disabledId = await factory.CreateUserAsync(
            disabledName,
            ExistingPassword,
            isDisabled: true);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        var publicConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            "SignalR public recipients");
        var privateConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "SignalR private recipients");
        await UpsertMemberAsync(adminClient, privateConversation.Id, memberId);
        var directConversation = await CreateDirectAsync(adminClient, memberId);
        var transport = new RecordingNewMessageTransport();
        using var recordingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<INewMessageTransport>();
                services.AddSingleton<INewMessageTransport>(transport);
            }));
        _ = recordingFactory.CreateClient();

        await using (var scope = recordingFactory.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<NewMessagePublisher>();
            await publisher.TryPublishAsync(
                CreateSyntheticMessage(publicConversation.Id, adminId, "public recipients"));
            await publisher.TryPublishAsync(
                CreateSyntheticMessage(privateConversation.Id, adminId, "private recipients"));
            await publisher.TryPublishAsync(
                CreateSyntheticMessage(directConversation.Id, adminId, "direct recipients"));
        }

        var deliveries = transport.Deliveries.ToArray();
        Assert.Equal(3, deliveries.Length);
        var publicRecipients = deliveries[0].RecipientUserIds;
        Assert.Contains(adminId.ToString("D"), publicRecipients);
        Assert.Contains(memberId.ToString("D"), publicRecipients);
        Assert.Contains(outsiderId.ToString("D"), publicRecipients);
        Assert.DoesNotContain(disabledId.ToString("D"), publicRecipients);
        Assert.Equal(
            new[] { adminId.ToString("D"), memberId.ToString("D") }.Order(),
            deliveries[1].RecipientUserIds.Order());
        Assert.Equal(
            new[] { adminId.ToString("D"), memberId.ToString("D") }.Order(),
            deliveries[2].RecipientUserIds.Order());
    }

    [Fact]
    public async Task MessageSend_WhenRealtimeTransportFails_RemainsCreatedAndDoesNotReplayPublish()
    {
        var adminName = CreateUserName("signalr-failure-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var transport = new ThrowingNewMessageTransport();
        using var failingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<INewMessageTransport>();
                services.AddSingleton<INewMessageTransport>(transport);
            }));
        using var client = await CreateAuthenticatedClientAsync(failingFactory, adminName);
        var conversation = await CreateChannelAsync(
            client,
            ConversationType.PublicChannel,
            "SignalR failure isolation");
        var request = CreateSendRequest(conversation.Id, "persist despite realtime failure");

        using (var createdResponse = await client.PostAsJsonAsync("/api/messages", request))
        {
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        }

        using (var replayResponse = await client.PostAsJsonAsync("/api/messages", request))
        {
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        }

        Assert.Equal(1, transport.AttemptCount);
        await using var scope = failingFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(
            1,
            await dbContext.Messages.CountAsync(message =>
                message.ClientMessageId == request.ClientMessageId));
    }

    private static HubConnection CreateHubConnection(
        RelayCoveWebApplicationFactory applicationFactory,
        string? accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(applicationFactory.Server.BaseAddress, ChatHub.Route),
                options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(accessToken);
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => applicationFactory.Server.CreateHandler();
                })
            .Build();

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        WebApplicationFactory<Program> applicationFactory,
        string userName)
    {
        var client = applicationFactory.CreateClient();
        var login = await LoginAsync(client, userName);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "signalr-tests", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task<ConversationDto> CreateChannelAsync(
        HttpClient client,
        ConversationType type,
        string name)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(type, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<ConversationDto> CreateDirectAsync(HttpClient client, Guid participantUserId)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: participantUserId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task UpsertMemberAsync(HttpClient client, Guid conversationId, Guid userId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/members",
            new UpsertConversationMemberRequest(userId, ConversationMemberRole.Member));
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created);
    }

    private static SendMessageRequest CreateSendRequest(Guid conversationId, string content) => new(
        Guid.NewGuid(),
        conversationId,
        MessageType.Text,
        content,
        null,
        [],
        []);

    private static MessageDto CreateSyntheticMessage(
        Guid conversationId,
        Guid senderId,
        string content) => new(
        Interlocked.Increment(ref syntheticMessageId),
        Guid.NewGuid(),
        conversationId,
        senderId,
        "SignalR test sender",
        MessageType.Text,
        content,
        null,
        [],
        [],
        DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The expected SignalR event was not observed within five seconds.");
    }

    private async Task WaitForGroupJoinAsync(int logOffset, Guid userId)
    {
        await WaitUntilAsync(() => factory.LogMessages
            .Skip(logOffset)
            .Any(message =>
                message.Contains(userId.ToString("D"), StringComparison.Ordinal) &&
                message.Contains("connected and joined", StringComparison.Ordinal)));
    }

    private static string CreateUserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed class ThrowingNewMessageTransport : INewMessageTransport
    {
        private int attemptCount;

        public int AttemptCount => Volatile.Read(ref attemptCount);

        public Task SendAsync(
            IReadOnlyList<string> recipientUserIds,
            MessageDto message,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            throw new InvalidOperationException("Synthetic SignalR transport failure.");
        }
    }

    private sealed class RecordingNewMessageTransport : INewMessageTransport
    {
        public ConcurrentQueue<RecordedDelivery> Deliveries { get; } = new();

        public Task SendAsync(
            IReadOnlyList<string> recipientUserIds,
            MessageDto message,
            CancellationToken cancellationToken)
        {
            Deliveries.Enqueue(new RecordedDelivery(recipientUserIds.ToArray(), message));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedDelivery(
        IReadOnlyList<string> RecipientUserIds,
        MessageDto Message);
}
