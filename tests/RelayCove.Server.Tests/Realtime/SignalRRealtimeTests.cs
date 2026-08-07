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
using RelayCove.Shared.Admin;
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
        var accessGrants = new ConcurrentQueue<Guid>();

        await using var connection = CreateHubConnection(factory, memberLogin.AccessToken);
        connection.On<MessageDto>(nameof(IChatClient.NewMessage), received.Enqueue);
        connection.On<Guid>(nameof(IChatClient.ConversationAccessGranted), accessGrants.Enqueue);
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
        await WaitUntilAsync(() => accessGrants.Contains(joinedAfterConnection.Id));
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
    public async Task MessageSend_WhenFileIsAttached_PublishesTheCanonicalAttachmentPayload()
    {
        var adminName = CreateUserName("signalr-attachment-admin");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(factory, adminName);
        var login = await LoginAsync(factory.CreateClient(), adminName);
        var conversation = await CreateChannelAsync(
            client,
            ConversationType.PublicChannel,
            "SignalR attachment delivery");
        var received = new ConcurrentQueue<MessageDto>();
        await using var connection = CreateHubConnection(factory, login.AccessToken);
        connection.On<MessageDto>(nameof(IChatClient.NewMessage), received.Enqueue);
        var connectionLogOffset = factory.LogMessages.Count;
        await connection.StartAsync();
        await WaitForGroupJoinAsync(connectionLogOffset, adminId);

        using var form = new MultipartFormDataContent($"relaycove-{Guid.NewGuid():N}");
        var file = new ByteArrayContent([4, 2]);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(file, "file", "signalr-attachment.bin");
        using var uploadResponse = await client.PostAsync("/api/attachments", form);
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        var attachment = (await uploadResponse.Content.ReadFromJsonAsync<AttachmentDto>())!;
        var request = new SendMessageRequest(
            Guid.NewGuid(),
            conversation.Id,
            MessageType.File,
            null,
            null,
            [attachment.Id],
            []);

        using var response = await client.PostAsJsonAsync("/api/messages", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = (await response.Content.ReadFromJsonAsync<MessageDto>())!;
        await WaitUntilAsync(() => received.Any(message => message.ClientMessageId == request.ClientMessageId));
        var published = Assert.Single(received, message =>
            message.ClientMessageId == request.ClientMessageId);
        Assert.Equal(sent.Attachments, published.Attachments);
        Assert.Equal([attachment], published.Attachments);
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
        var logOffset = factory.LogMessages.Count;

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
        var publicRecipients = deliveries[0].Recipients;
        Assert.Contains(publicRecipients, recipient => recipient.UserId == adminId && recipient.AccessTokenVersion == 0);
        Assert.Contains(publicRecipients, recipient => recipient.UserId == memberId && recipient.AccessTokenVersion == 0);
        Assert.Contains(publicRecipients, recipient => recipient.UserId == outsiderId && recipient.AccessTokenVersion == 0);
        Assert.DoesNotContain(publicRecipients, recipient => recipient.UserId == disabledId);
        Assert.Equal(
            new[] { adminId, memberId }.Order(),
            deliveries[1].Recipients.Select(recipient => recipient.UserId).Order());
        Assert.Equal(
            new[] { adminId, memberId }.Order(),
            deliveries[2].Recipients.Select(recipient => recipient.UserId).Order());
        var recipientSelects = factory.LogMessages
            .Skip(logOffset)
            .Where(message =>
                message.Contains("Executed DbCommand", StringComparison.Ordinal) &&
                message.Contains("SELECT", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, recipientSelects.Length);
    }

    [Fact]
    public async Task NewMessagePublisher_WhenAccountTokenGenerationChanges_DeliversOnlyToCurrentGeneration()
    {
        var adminName = CreateUserName("signalr-generation-admin");
        var memberName = CreateUserName("signalr-generation-member");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        var conversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            "SignalR generation isolation");
        var oldLogin = await LoginAsync(factory.CreateClient(), memberName);
        var staleMessages = new ConcurrentQueue<MessageDto>();

        await using var staleConnection = CreateHubConnection(factory, oldLogin.AccessToken);
        staleConnection.On<MessageDto>(nameof(IChatClient.NewMessage), staleMessages.Enqueue);
        var initialConnectionLogOffset = factory.LogMessages.Count;
        await staleConnection.StartAsync();
        await WaitForGroupJoinAsync(initialConnectionLogOffset, memberId);

        using (var disable = await adminClient.PutAsJsonAsync(
                   $"/api/admin/users/{memberId:D}",
                   new UpdateAdminUserRequest(true)))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }

        using (var restore = await adminClient.PutAsJsonAsync(
                   $"/api/admin/users/{memberId:D}",
                   new UpdateAdminUserRequest(false)))
        {
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        }

        var currentLogin = await LoginAsync(factory.CreateClient(), memberName);
        Assert.Equal(2, currentLogin.AccessTokenVersion);
        var currentMessages = new ConcurrentQueue<MessageDto>();
        await using var currentConnection = CreateHubConnection(factory, currentLogin.AccessToken);
        currentConnection.On<MessageDto>(nameof(IChatClient.NewMessage), currentMessages.Enqueue);
        var currentConnectionLogOffset = factory.LogMessages.Count;
        await currentConnection.StartAsync();
        await WaitForGroupJoinAsync(currentConnectionLogOffset, memberId);

        var message = CreateSyntheticMessage(conversation.Id, adminId, "current generation only");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<NewMessagePublisher>();
            await publisher.TryPublishAsync(message);
        }

        await WaitUntilAsync(() => currentMessages.Any(candidate => candidate.Id == message.Id));
        await Task.Delay(200);
        Assert.DoesNotContain(staleMessages, candidate => candidate.Id == message.Id);
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

    [Fact]
    public async Task ConversationAccessRevoked_WhenPrivateMemberIsRemoved_ReachesTargetConnectionsAndStopsNewMessages()
    {
        var adminName = CreateUserName("access-revoked-admin");
        var memberName = CreateUserName("access-revoked-member");
        var outsiderName = CreateUserName("access-revoked-outsider");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        await factory.CreateUserAsync(outsiderName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(factory, memberName);
        var adminLogin = await LoginAsync(factory.CreateClient(), adminName);
        var memberLogin = await LoginAsync(factory.CreateClient(), memberName);
        var outsiderLogin = await LoginAsync(factory.CreateClient(), outsiderName);
        var conversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "Realtime access revoked");
        await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        var firstTargetEvents = new ConcurrentQueue<Guid>();
        var secondTargetEvents = new ConcurrentQueue<Guid>();
        var adminEvents = new ConcurrentQueue<Guid>();
        var outsiderEvents = new ConcurrentQueue<Guid>();
        var firstTargetMessages = new ConcurrentQueue<MessageDto>();
        var secondTargetMessages = new ConcurrentQueue<MessageDto>();
        var adminMessages = new ConcurrentQueue<MessageDto>();
        await using var firstTargetConnection = CreateHubConnection(factory, memberLogin.AccessToken);
        await using var secondTargetConnection = CreateHubConnection(factory, memberLogin.AccessToken);
        await using var adminConnection = CreateHubConnection(factory, adminLogin.AccessToken);
        await using var outsiderConnection = CreateHubConnection(factory, outsiderLogin.AccessToken);
        firstTargetConnection.On<Guid>(
            nameof(IChatClient.ConversationAccessRevoked),
            firstTargetEvents.Enqueue);
        secondTargetConnection.On<Guid>(
            nameof(IChatClient.ConversationAccessRevoked),
            secondTargetEvents.Enqueue);
        adminConnection.On<Guid>(nameof(IChatClient.ConversationAccessRevoked), adminEvents.Enqueue);
        outsiderConnection.On<Guid>(nameof(IChatClient.ConversationAccessRevoked), outsiderEvents.Enqueue);
        firstTargetConnection.On<MessageDto>(nameof(IChatClient.NewMessage), firstTargetMessages.Enqueue);
        secondTargetConnection.On<MessageDto>(nameof(IChatClient.NewMessage), secondTargetMessages.Enqueue);
        adminConnection.On<MessageDto>(nameof(IChatClient.NewMessage), adminMessages.Enqueue);
        await Task.WhenAll(
            firstTargetConnection.StartAsync(),
            secondTargetConnection.StartAsync(),
            adminConnection.StartAsync(),
            outsiderConnection.StartAsync());

        using (var deleteResponse = await adminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }

        await WaitUntilAsync(() => firstTargetEvents.Count == 1 && secondTargetEvents.Count == 1);
        Assert.Equal(conversation.Id, firstTargetEvents.Single());
        Assert.Equal(conversation.Id, secondTargetEvents.Single());
        Assert.Empty(adminEvents);
        Assert.Empty(outsiderEvents);

        using (var deniedHistory = await memberClient.GetAsync(
                   $"/api/conversations/{conversation.Id:D}/messages"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedHistory.StatusCode);
        }

        MessageDto created;
        using (var nextMessageResponse = await adminClient.PostAsJsonAsync(
                   "/api/messages",
                   CreateSendRequest(conversation.Id, "after realtime revocation")))
        {
            Assert.Equal(HttpStatusCode.Created, nextMessageResponse.StatusCode);
            created = (await nextMessageResponse.Content.ReadFromJsonAsync<MessageDto>())!;
        }

        await WaitUntilAsync(() => adminMessages.Any(message => message.Id == created.Id));
        var barrier = CreateSyntheticMessage(conversation.Id, adminId, "revoked target barrier");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub, IChatClient>>();
            await hubContext.Clients.User(memberId.ToString("D")).NewMessage(barrier);
        }

        await WaitUntilAsync(() =>
            firstTargetMessages.Any(message => message.Id == barrier.Id) &&
            secondTargetMessages.Any(message => message.Id == barrier.Id));
        Assert.DoesNotContain(firstTargetMessages, message => message.Id == created.Id);
        Assert.DoesNotContain(secondTargetMessages, message => message.Id == created.Id);
    }

    [Fact]
    public async Task ConversationAccessRevoked_WhenDeleteIsConcurrentOrRepeated_PublishesOnce()
    {
        var adminName = CreateUserName("access-revoked-race-admin");
        var memberName = CreateUserName("access-revoked-race-member");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        var memberLogin = await LoginAsync(factory.CreateClient(), memberName);
        var conversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "Realtime access revoked race");
        await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        var revokedEvents = new ConcurrentQueue<Guid>();
        var barrierMessages = new ConcurrentQueue<MessageDto>();
        await using var connection = CreateHubConnection(factory, memberLogin.AccessToken);
        connection.On<Guid>(nameof(IChatClient.ConversationAccessRevoked), revokedEvents.Enqueue);
        connection.On<MessageDto>(nameof(IChatClient.NewMessage), barrierMessages.Enqueue);
        await connection.StartAsync();
        var path = $"/api/conversations/{conversation.Id:D}/members/{memberId:D}";

        var firstDeleteTask = adminClient.DeleteAsync(path);
        var secondDeleteTask = adminClient.DeleteAsync(path);
        using var firstDeleteResponse = await firstDeleteTask;
        using var secondDeleteResponse = await secondDeleteTask;
        Assert.Equal(HttpStatusCode.NoContent, firstDeleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondDeleteResponse.StatusCode);
        using (var repeatedDeleteResponse = await adminClient.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.NoContent, repeatedDeleteResponse.StatusCode);
        }

        var barrier = CreateSyntheticMessage(conversation.Id, memberId, "delete race barrier");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub, IChatClient>>();
            await hubContext.Clients.User(memberId.ToString("D")).NewMessage(barrier);
        }

        await WaitUntilAsync(() => barrierMessages.Any(message => message.Id == barrier.Id));
        Assert.Equal([conversation.Id], revokedEvents);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var dbContext = verificationScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.False(await dbContext.ConversationMembers.AnyAsync(member =>
            member.ConversationId == conversation.Id && member.UserId == memberId));
    }

    [Fact]
    public async Task ConversationAccessRevoked_WhenTransportFails_PreservesRemovalAndNoReplayPublish()
    {
        var adminName = CreateUserName("access-revoked-failure-admin");
        var memberName = CreateUserName("access-revoked-failure-member");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        using var setupClient = await CreateAuthenticatedClientAsync(factory, adminName);
        var conversation = await CreateChannelAsync(
            setupClient,
            ConversationType.PrivateChannel,
            "Realtime access revoked failure");
        await UpsertMemberAsync(setupClient, conversation.Id, memberId);
        await factory.SetUserDisabledAsync(memberId, isDisabled: true);
        var transport = new ThrowingAccessRevokedTransport();
        using var failingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConversationAccessRevokedTransport>();
                services.AddSingleton<IConversationAccessRevokedTransport>(transport);
            }));
        using var client = await CreateAuthenticatedClientAsync(failingFactory, adminName);
        var path = $"/api/conversations/{conversation.Id:D}/members/{memberId:D}";
        var logOffset = factory.LogMessages.Count;

        using (var deleteResponse = await client.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }

        using (var replayResponse = await client.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.NoContent, replayResponse.StatusCode);
        }

        Assert.Equal(1, transport.AttemptCount);
        Assert.Equal(memberId.ToString("D"), transport.RecipientUserId);
        Assert.Equal(conversation.Id, transport.ConversationId);
        await using var scope = failingFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.False(await dbContext.ConversationMembers.AnyAsync(member =>
            member.ConversationId == conversation.Id && member.UserId == memberId));
        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message =>
                message.Contains(adminName, StringComparison.Ordinal) ||
                message.Contains(memberName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConversationAccessRevoked_WhenRemovalDoesNotDeleteMember_DoesNotPublish()
    {
        var adminName = CreateUserName("access-revoked-negative-admin");
        var memberName = CreateUserName("access-revoked-negative-member");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        using var setupClient = await CreateAuthenticatedClientAsync(factory, adminName);
        var publicConversation = await CreateChannelAsync(
            setupClient,
            ConversationType.PublicChannel,
            "Realtime access revoked public");
        var privateConversation = await CreateChannelAsync(
            setupClient,
            ConversationType.PrivateChannel,
            "Realtime access revoked absent");
        var directConversation = await CreateDirectAsync(setupClient, memberId);
        var transport = new RecordingAccessRevokedTransport();
        using var recordingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConversationAccessRevokedTransport>();
                services.AddSingleton<IConversationAccessRevokedTransport>(transport);
            }));
        using var client = await CreateAuthenticatedClientAsync(recordingFactory, adminName);

        using (var publicResponse = await client.DeleteAsync(
                   $"/api/conversations/{publicConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, publicResponse.StatusCode);
        }

        using (var directResponse = await client.DeleteAsync(
                   $"/api/conversations/{directConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, directResponse.StatusCode);
        }

        using (var unknownResponse = await client.DeleteAsync(
                   $"/api/conversations/{Guid.NewGuid():D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unknownResponse.StatusCode);
        }

        using (var absentMemberResponse = await client.DeleteAsync(
                   $"/api/conversations/{privateConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, absentMemberResponse.StatusCode);
        }

        using (var invalidTargetResponse = await client.DeleteAsync(
                   $"/api/conversations/{privateConversation.Id:D}/members/{Guid.Empty:D}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidTargetResponse.StatusCode);
        }

        Assert.Equal(0, transport.AttemptCount);
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
            IReadOnlyList<NewMessageRecipient> recipients,
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
            IReadOnlyList<NewMessageRecipient> recipients,
            MessageDto message,
            CancellationToken cancellationToken)
        {
            Deliveries.Enqueue(new RecordedDelivery(recipients.ToArray(), message));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedDelivery(
        IReadOnlyList<NewMessageRecipient> Recipients,
        MessageDto Message);

    private sealed class ThrowingAccessRevokedTransport : IConversationAccessRevokedTransport
    {
        private int attemptCount;

        public int AttemptCount => Volatile.Read(ref attemptCount);

        public string? RecipientUserId { get; private set; }

        public Guid ConversationId { get; private set; }

        public Task SendAsync(
            string recipientUserId,
            Guid conversationId,
            CancellationToken cancellationToken)
        {
            RecipientUserId = recipientUserId;
            ConversationId = conversationId;
            Interlocked.Increment(ref attemptCount);
            throw new InvalidOperationException("Synthetic access-revoked transport failure.");
        }
    }

    private sealed class RecordingAccessRevokedTransport : IConversationAccessRevokedTransport
    {
        private int attemptCount;

        public int AttemptCount => Volatile.Read(ref attemptCount);

        public Task SendAsync(
            string recipientUserId,
            Guid conversationId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            return Task.CompletedTask;
        }
    }
}
