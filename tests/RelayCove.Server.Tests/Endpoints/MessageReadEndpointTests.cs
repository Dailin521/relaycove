using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class MessageReadEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string ExistingPassword = "a secure read-through test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadEndpoint_WhenUnauthenticatedOrRequestInvalid_ReturnsStableErrors()
    {
        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.PostAsJsonAsync(
                   $"/api/conversations/{Guid.NewGuid():D}/read",
                   new MarkConversationReadRequest(1)))
        {
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
        }

        var adminName = CreateUserName("read-validation-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Read validation");

        foreach (var invalidMessageId in new long[] { 0, -1 })
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/conversations/{conversation.Id:D}/read",
                new MarkConversationReadRequest(invalidMessageId));
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task PublicRead_WhenActorHasNoStateRow_CreatesPrivateWatermarkAndUpdatesUnreadMonotonically()
    {
        var adminName = CreateUserName("public-read-admin");
        var readerName = CreateUserName("public-read-reader");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var readerId = await factory.CreateUserAsync(readerName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var readerClient = await CreateAuthenticatedClientAsync(readerName);
        var conversation = await CreateChannelAsync(
            adminClient, ConversationType.PublicChannel, "Public read state");
        const string secret = "read-through-secret-42f83d";
        var first = await SendAsync(adminClient, conversation.Id, "first");
        await SendAsync(readerClient, conversation.Id, "reader's own message");
        var second = await SendAsync(adminClient, conversation.Id, secret);
        var third = await SendAsync(adminClient, conversation.Id, "third");
        var initialView = await GetConversationAsync(readerClient, conversation.Id);
        Assert.Equal(3, initialView.UnreadCount);
        Assert.Equal(0, initialView.LastReadMessageId);

        DateTime updatedAtBeforeRead;
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var beforeContext = beforeScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            updatedAtBeforeRead = (await beforeContext.Conversations
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == conversation.Id)).UpdatedAt;
        }

        var logOffset = factory.LogMessages.Count;
        var firstReadTask = MarkReadAsync(readerClient, conversation.Id, first.Id);
        var secondReadTask = MarkReadAsync(readerClient, conversation.Id, second.Id);
        var initialReceipts = await Task.WhenAll(firstReadTask, secondReadTask);
        Assert.True(initialReceipts[0].LastReadMessageId >= first.Id);
        var receipt = initialReceipts[1];
        Assert.Equal(conversation.Id, receipt.ConversationId);
        Assert.Equal(second.Id, receipt.LastReadMessageId);
        var afterSecond = await GetConversationAsync(readerClient, conversation.Id);
        Assert.Equal(second.Id, afterSecond.LastReadMessageId);
        Assert.Equal(1, afterSecond.UnreadCount);

        Assert.Equal(second.Id, (await MarkReadAsync(readerClient, conversation.Id, first.Id)).LastReadMessageId);
        Assert.Equal(second.Id, (await MarkReadAsync(readerClient, conversation.Id, second.Id)).LastReadMessageId);
        Assert.Equal(third.Id, (await MarkReadAsync(readerClient, conversation.Id, third.Id)).LastReadMessageId);
        Assert.Equal(0, (await GetConversationAsync(readerClient, conversation.Id)).UnreadCount);

        using (var membersResponse = await readerClient.GetAsync(
                   $"/api/conversations/{conversation.Id:D}/members"))
        {
            await AssertErrorAsync(
                membersResponse,
                HttpStatusCode.Conflict,
                ApiErrorCodes.ConversationTypeConflict);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var state = await dbContext.ConversationMembers.AsNoTracking().SingleAsync(member =>
            member.ConversationId == conversation.Id && member.UserId == readerId);
        Assert.Equal(ConversationMemberRole.Member, state.Role);
        Assert.Equal(third.Id, state.LastReadMessageId);
        Assert.Equal(updatedAtBeforeRead, (await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversation.Id)).UpdatedAt);
        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message => message.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message => message.Contains(readerName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrivateAndDirectRead_WhenConcurrentOrRevoked_PreserveMembershipAndMaximumTarget()
    {
        var adminName = CreateUserName("member-read-admin");
        var memberName = CreateUserName("member-read-user");
        var directName = CreateUserName("direct-read-user");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        var directId = await factory.CreateUserAsync(directName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        using var directClient = await CreateAuthenticatedClientAsync(directName);
        var privateConversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Private read state");
        await UpsertMemberAsync(adminClient, privateConversation.Id, memberId);
        var first = await SendAsync(adminClient, privateConversation.Id, "first");
        var second = await SendAsync(adminClient, privateConversation.Id, "second");
        var third = await SendAsync(adminClient, privateConversation.Id, "third");

        var firstTask = MarkReadAsync(memberClient, privateConversation.Id, first.Id);
        var thirdTask = MarkReadAsync(memberClient, privateConversation.Id, third.Id);
        var receipts = await Task.WhenAll(firstTask, thirdTask);
        Assert.All(receipts, receipt => Assert.True(receipt.LastReadMessageId >= first.Id));
        Assert.Contains(receipts, receipt => receipt.LastReadMessageId == third.Id);
        Assert.Equal(third.Id, (await MarkReadAsync(memberClient, privateConversation.Id, second.Id)).LastReadMessageId);
        var privateView = await GetConversationAsync(memberClient, privateConversation.Id);
        Assert.Equal(third.Id, privateView.LastReadMessageId);
        Assert.Equal(0, privateView.UnreadCount);

        using (var removeResponse = await adminClient.DeleteAsync(
                   $"/api/conversations/{privateConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);
        }

        using (var revokedResponse = await memberClient.PostAsJsonAsync(
                   $"/api/conversations/{privateConversation.Id:D}/read",
                   new MarkConversationReadRequest(first.Id)))
        {
            await AssertErrorAsync(
                revokedResponse,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        var directConversation = await CreateDirectAsync(adminClient, directId);
        var directMessage = await SendAsync(adminClient, directConversation.Id, "direct");
        Assert.Equal(
            directMessage.Id,
            (await MarkReadAsync(directClient, directConversation.Id, directMessage.Id)).LastReadMessageId);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(2, await dbContext.ConversationMembers.CountAsync(
            member => member.ConversationId == directConversation.Id));
    }

    [Fact]
    public async Task Read_WhenTargetOrAccessIsInvalid_FailsClosedBeforeCreatingState()
    {
        var adminName = CreateUserName("read-boundary-admin");
        var readerName = CreateUserName("read-boundary-reader");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var readerId = await factory.CreateUserAsync(readerName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var readerClient = await CreateAuthenticatedClientAsync(readerName);
        var firstConversation = await CreateChannelAsync(
            adminClient, ConversationType.PublicChannel, "Read boundary A");
        var secondConversation = await CreateChannelAsync(
            adminClient, ConversationType.PublicChannel, "Read boundary B");
        var firstMessage = await SendAsync(adminClient, firstConversation.Id, "first");
        var secondMessage = await SendAsync(adminClient, secondConversation.Id, "second");

        foreach (var invalidTarget in new[] { secondMessage.Id, long.MaxValue })
        {
            using var invalidResponse = await readerClient.PostAsJsonAsync(
                $"/api/conversations/{firstConversation.Id:D}/read",
                new MarkConversationReadRequest(invalidTarget));
            await AssertErrorAsync(invalidResponse, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        using (var unknownResponse = await readerClient.PostAsJsonAsync(
                   $"/api/conversations/{Guid.NewGuid():D}/read",
                   new MarkConversationReadRequest(firstMessage.Id)))
        {
            await AssertErrorAsync(
                unknownResponse,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        var privateConversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Read boundary private");
        var privateMessage = await SendAsync(adminClient, privateConversation.Id, "private");
        foreach (var target in new[] { privateMessage.Id, long.MaxValue })
        {
            using var deniedResponse = await readerClient.PostAsJsonAsync(
                $"/api/conversations/{privateConversation.Id:D}/read",
                new MarkConversationReadRequest(target));
            await AssertErrorAsync(
                deniedResponse,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        await using (var deleteScope = factory.Services.CreateAsyncScope())
        {
            var deleteContext = deleteScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var stored = await deleteContext.Conversations.SingleAsync(
                candidate => candidate.Id == privateConversation.Id);
            stored.MarkDeleted(stored.UpdatedAt);
            await deleteContext.SaveChangesAsync();
        }

        using (var deletedResponse = await adminClient.PostAsJsonAsync(
                   $"/api/conversations/{privateConversation.Id:D}/read",
                   new MarkConversationReadRequest(privateMessage.Id)))
        {
            await AssertErrorAsync(
                deletedResponse,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        await using (var verificationScope = factory.Services.CreateAsyncScope())
        {
            var verificationContext = verificationScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            Assert.False(await verificationContext.ConversationMembers.AnyAsync(member =>
                member.ConversationId == firstConversation.Id && member.UserId == readerId));
        }

        await factory.SetUserDisabledAsync(readerId, true);
        using var disabledResponse = await readerClient.PostAsJsonAsync(
            $"/api/conversations/{firstConversation.Id:D}/read",
            new MarkConversationReadRequest(firstMessage.Id));
        await AssertErrorAsync(
            disabledResponse,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task ReadWrite_WhenSqliteIsBusy_ReturnsStableServiceUnavailable()
    {
        using var busyFactory = new RelayCoveWebApplicationFactory(1_000, 1_000, databaseTimeoutSeconds: 1);
        await busyFactory.InitializeDatabaseAsync();
        var adminName = CreateUserName("read-busy-admin");
        await busyFactory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(busyFactory, adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Busy read");
        var message = await SendAsync(client, conversation.Id, "busy");
        await using var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = busyFactory.DatabasePath,
            DefaultTimeout = 1,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        await lockConnection.OpenAsync();
        await using var lockTransaction = lockConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id:D}/read",
            new MarkConversationReadRequest(message.Id));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServiceUnavailable);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string userName) =>
        await CreateAuthenticatedClientAsync(factory, userName);

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        RelayCoveWebApplicationFactory applicationFactory,
        string userName)
    {
        var client = applicationFactory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "read-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
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
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<MessageDto> SendAsync(
        HttpClient client,
        Guid conversationId,
        string content)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/messages",
            new SendMessageRequest(
                Guid.NewGuid(),
                conversationId,
                MessageType.Text,
                content,
                null,
                [],
                []));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>())!;
    }

    private static async Task<ConversationReadReceipt> MarkReadAsync(
        HttpClient client,
        Guid conversationId,
        long messageId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/read",
            new MarkConversationReadRequest(messageId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationReadReceipt>())!;
    }

    private static async Task<ConversationDto> GetConversationAsync(
        HttpClient client,
        Guid conversationId)
    {
        using var response = await client.GetAsync($"/api/conversations/{conversationId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(expectedCode, error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }

    private static string CreateUserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
