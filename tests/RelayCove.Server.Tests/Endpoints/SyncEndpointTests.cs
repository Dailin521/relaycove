using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class SyncEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string ExistingPassword = "a secure sync test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SyncEndpoint_WhenUnauthenticatedInvalidEmptyOrFuture_ReturnsStableResults()
    {
        using var emptyFactory = new RelayCoveWebApplicationFactory();
        await emptyFactory.InitializeDatabaseAsync();
        using (var anonymous = emptyFactory.CreateClient())
        using (var response = await anonymous.GetAsync("/api/sync?cursor=0"))
        {
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
        }

        var userName = CreateUserName("sync-validation");
        var userId = await emptyFactory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(emptyFactory, userName);
        foreach (var query in new[]
                 {
                     string.Empty,
                     "?cursor=-1",
                     "?cursor=1&snapshotUpperBound=0",
                     "?cursor=0&snapshotUpperBound=-1",
                     "?cursor=0&limit=0",
                     "?cursor=0&limit=201",
                     "?cursor=not-a-number",
                     "?cursor=0&limit=not-a-number",
                 })
        {
            using var invalid = await client.GetAsync($"/api/sync{query}");
            await AssertErrorAsync(invalid, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        var empty = await GetSyncAsync(client, cursor: 0);
        Assert.Empty(empty.Messages);
        Assert.Equal(0, empty.NextCursor);
        Assert.Equal(0, empty.SnapshotUpperBound);
        Assert.False(empty.HasMore);
        AssertSyncInvariants(empty, requestCursor: 0);

        foreach (var futureQuery in new[]
                 {
                     "/api/sync?cursor=1",
                     "/api/sync?cursor=0&snapshotUpperBound=1",
                 })
        {
            using var future = await client.GetAsync(futureQuery);
            await AssertErrorAsync(future, HttpStatusCode.Conflict, ApiErrorCodes.SyncCursorInvalid);
        }

        await emptyFactory.SetUserDisabledAsync(userId, true);
        using var disabled = await client.GetAsync("/api/sync?cursor=0");
        await AssertErrorAsync(disabled, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
        await using var disabledScope = emptyFactory.Services.CreateAsyncScope();
        var disabledService = disabledScope.ServiceProvider.GetRequiredService<MessageSyncService>();
        Assert.Equal(
            SyncOperationStatus.AuthenticationUnavailable,
            (await disabledService.GetPageAsync(
                userId,
                cursor: 0,
                snapshotUpperBound: null,
                limit: 1,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Sync_WhenPagedAcrossPermissionHoles_PreservesSnapshotAndAdvancesAuthoritativeCursor()
    {
        var adminName = CreateUserName("sync-page-admin");
        var readerName = CreateUserName("sync-page-reader");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var readerId = await factory.CreateUserAsync(readerName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var readerClient = await CreateAuthenticatedClientAsync(readerName);
        var baselineCursor = await GetCurrentMaximumMessageIdAsync();
        var publicConversation = await CreateChannelAsync(
            adminClient, ConversationType.PublicChannel, "Sync public");
        var privateConversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Sync holes");
        var first = await SendAsync(adminClient, CreateSendRequest(publicConversation.Id, "public 1"));
        await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "private hole 1"));
        var second = await SendAsync(
            adminClient,
            CreateSendRequest(publicConversation.Id, "public 2") with { MentionUserIds = [readerId] });
        await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "private hole 2"));
        var third = await SendAsync(adminClient, CreateSendRequest(publicConversation.Id, "public 3"));
        var snapshotTail = await SendAsync(
            adminClient,
            CreateSendRequest(privateConversation.Id, "private trailing hole"));

        var logOffset = factory.LogMessages.Count;
        var firstPage = await GetSyncAsync(readerClient, cursor: baselineCursor, limit: 2);
        Assert.Equal([first.Id, second.Id], firstPage.Messages.Select(message => message.Id));
        Assert.Equal([readerId], firstPage.Messages[^1].MentionUserIds);
        Assert.Equal(second.Id, firstPage.NextCursor);
        Assert.Equal(snapshotTail.Id, firstPage.SnapshotUpperBound);
        Assert.True(firstPage.HasMore);
        AssertSyncInvariants(firstPage, requestCursor: baselineCursor);
        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message => message.Contains("public 2", StringComparison.Ordinal));
        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message => message.Contains(readerName, StringComparison.Ordinal));

        var afterSnapshot = await SendAsync(
            adminClient,
            CreateSendRequest(publicConversation.Id, "after fixed snapshot"));
        var secondPage = await GetSyncAsync(
            readerClient,
            firstPage.NextCursor,
            firstPage.SnapshotUpperBound,
            limit: 2);
        Assert.Equal([third.Id], secondPage.Messages.Select(message => message.Id));
        Assert.Equal(firstPage.SnapshotUpperBound, secondPage.NextCursor);
        Assert.Equal(firstPage.SnapshotUpperBound, secondPage.SnapshotUpperBound);
        Assert.False(secondPage.HasMore);
        AssertSyncInvariants(secondPage, firstPage.NextCursor);

        var nextRound = await GetSyncAsync(readerClient, firstPage.SnapshotUpperBound, limit: 2);
        Assert.Equal([afterSnapshot.Id], nextRound.Messages.Select(message => message.Id));
        Assert.Equal(afterSnapshot.Id, nextRound.NextCursor);
        Assert.Equal(afterSnapshot.Id, nextRound.SnapshotUpperBound);
        Assert.False(nextRound.HasMore);
        AssertSyncInvariants(nextRound, firstPage.SnapshotUpperBound);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MessageSyncService>();
        var serviceLogOffset = factory.LogMessages.Count;
        var serviceResult = await service.GetPageAsync(
            readerId,
            cursor: baselineCursor,
            snapshotUpperBound: null,
            limit: 2,
            CancellationToken.None);
        Assert.Equal(SyncOperationStatus.Success, serviceResult.Status);
        var selectCommands = factory.LogMessages
            .Skip(serviceLogOffset)
            .Where(message =>
                message.Contains("Executed DbCommand", StringComparison.Ordinal) &&
                message.Contains("SELECT", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, selectCommands.Length);
    }

    [Fact]
    public async Task Sync_WhenOnlyInaccessibleMessagesExist_UsesEmptyPageToCrossGlobalHoles()
    {
        var adminName = CreateUserName("sync-hole-admin");
        var outsiderName = CreateUserName("sync-hole-outsider");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        await factory.CreateUserAsync(outsiderName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var outsiderClient = await CreateAuthenticatedClientAsync(outsiderName);
        var baselineCursor = await GetCurrentMaximumMessageIdAsync();
        var privateConversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Only holes");
        var last = await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "hidden 1"));
        var deletedPublicConversation = await CreateChannelAsync(
            adminClient, ConversationType.PublicChannel, "Deleted public hole");
        last = await SendAsync(adminClient, CreateSendRequest(deletedPublicConversation.Id, "deleted public"));
        last = await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "hidden 2"));
        await using (var deleteScope = factory.Services.CreateAsyncScope())
        {
            var dbContext = deleteScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var deletedPublic = await dbContext.Conversations.SingleAsync(
                conversation => conversation.Id == deletedPublicConversation.Id);
            deletedPublic.MarkDeleted(deletedPublic.UpdatedAt);
            await dbContext.SaveChangesAsync();
        }

        var response = await GetSyncAsync(outsiderClient, cursor: baselineCursor, limit: 1);

        Assert.Empty(response.Messages);
        Assert.Equal(last.Id, response.SnapshotUpperBound);
        Assert.Equal(last.Id, response.NextCursor);
        Assert.False(response.HasMore);
        AssertSyncInvariants(response, requestCursor: baselineCursor);

        var completed = await GetSyncAsync(
            outsiderClient,
            response.NextCursor,
            response.SnapshotUpperBound,
            limit: 1);
        Assert.Empty(completed.Messages);
        Assert.Equal(response.SnapshotUpperBound, completed.NextCursor);
        Assert.False(completed.HasMore);
        AssertSyncInvariants(completed, response.NextCursor);
    }

    [Fact]
    public async Task Sync_WhenMembershipAndWatermarksChange_RechecksEachSourceAndPage()
    {
        var adminName = CreateUserName("sync-access-admin");
        var memberName = CreateUserName("sync-access-member");
        var outsiderAdminName = CreateUserName("sync-access-outsider-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        await factory.CreateUserAsync(outsiderAdminName, ExistingPassword, isAdmin: true);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        using var outsiderAdminClient = await CreateAuthenticatedClientAsync(outsiderAdminName);
        var baselineCursor = await GetCurrentMaximumMessageIdAsync();
        var privateConversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Sync watermark");
        var oldFirst = await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "old 1"));
        var oldLast = await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "old 2"));
        var publicConversation = await CreateChannelAsync(
            adminClient, ConversationType.PublicChannel, "Sync public state");
        var publicMessage = await SendAsync(adminClient, CreateSendRequest(publicConversation.Id, "public"));
        await UpsertMemberAsync(adminClient, privateConversation.Id, memberId);
        var newPrivate = await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "new private"));
        var directConversation = await CreateDirectAsync(adminClient, memberId);
        var directMessage = await SendAsync(adminClient, CreateSendRequest(directConversation.Id, "direct"));
        await MarkReadAsync(memberClient, publicConversation.Id, publicMessage.Id);
        await MarkReadAsync(memberClient, directConversation.Id, directMessage.Id);

        var joined = await GetSyncAsync(memberClient, cursor: baselineCursor, limit: 20);
        Assert.Equal(
            new[] { publicMessage.Id, newPrivate.Id, directMessage.Id }.Order(),
            joined.Messages.Select(message => message.Id));
        Assert.DoesNotContain(joined.Messages, message => message.Id == oldFirst.Id || message.Id == oldLast.Id);
        AssertSyncInvariants(joined, requestCursor: baselineCursor);

        var beforeWatermarkAdvance = await GetSyncAsync(memberClient, cursor: baselineCursor, limit: 1);
        Assert.Equal([publicMessage.Id], beforeWatermarkAdvance.Messages.Select(message => message.Id));
        Assert.True(beforeWatermarkAdvance.HasMore);
        await MarkReadAsync(memberClient, privateConversation.Id, newPrivate.Id);
        var afterWatermarkAdvance = await GetSyncAsync(
            memberClient,
            beforeWatermarkAdvance.NextCursor,
            beforeWatermarkAdvance.SnapshotUpperBound,
            limit: 20);
        Assert.DoesNotContain(
            afterWatermarkAdvance.Messages,
            message => message.ConversationId == privateConversation.Id);
        Assert.Equal([directMessage.Id], afterWatermarkAdvance.Messages.Select(message => message.Id));
        Assert.False(afterWatermarkAdvance.HasMore);
        AssertSyncInvariants(afterWatermarkAdvance, beforeWatermarkAdvance.NextCursor);

        var history = await GetHistoryAsync(memberClient, privateConversation.Id);
        Assert.Equal(
            new[] { oldFirst.Id, oldLast.Id, newPrivate.Id },
            history.Messages.Select(message => message.Id));

        var outsider = await GetSyncAsync(outsiderAdminClient, cursor: baselineCursor, limit: 20);
        Assert.DoesNotContain(outsider.Messages, message => message.ConversationId == privateConversation.Id);

        using (var remove = await adminClient.DeleteAsync(
                   $"/api/conversations/{privateConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        }

        var revoked = await GetSyncAsync(memberClient, cursor: baselineCursor, limit: 20);
        Assert.DoesNotContain(revoked.Messages, message => message.ConversationId == privateConversation.Id);
        Assert.Contains(revoked.Messages, message => message.Id == publicMessage.Id);
        Assert.Contains(revoked.Messages, message => message.Id == directMessage.Id);

        await UpsertMemberAsync(adminClient, privateConversation.Id, memberId);
        var afterRejoin = await SendAsync(adminClient, CreateSendRequest(privateConversation.Id, "after rejoin"));
        var rejoined = await GetSyncAsync(memberClient, cursor: baselineCursor, limit: 20);
        Assert.DoesNotContain(rejoined.Messages, message => message.Id == newPrivate.Id);
        Assert.Contains(rejoined.Messages, message => message.Id == afterRejoin.Id);
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
            new LoginRequest(userName, ExistingPassword, "sync-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private async Task<long> GetCurrentMaximumMessageIdAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        return await dbContext.Messages
            .Select(message => (long?)message.Id)
            .MaxAsync() ?? 0L;
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

    private static SendMessageRequest CreateSendRequest(Guid conversationId, string content) => new(
        Guid.NewGuid(),
        conversationId,
        MessageType.Text,
        content,
        null,
        [],
        []);

    private static async Task<MessageDto> SendAsync(HttpClient client, SendMessageRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/messages", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>())!;
    }

    private static async Task MarkReadAsync(HttpClient client, Guid conversationId, long messageId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/read",
            new MarkConversationReadRequest(messageId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<MessageHistoryResponse> GetHistoryAsync(
        HttpClient client,
        Guid conversationId)
    {
        using var response = await client.GetAsync($"/api/conversations/{conversationId:D}/messages");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageHistoryResponse>())!;
    }

    private static async Task<SyncResponse> GetSyncAsync(
        HttpClient client,
        long cursor,
        long? snapshotUpperBound = null,
        int? limit = null)
    {
        var query = $"?cursor={cursor}";
        if (snapshotUpperBound.HasValue)
        {
            query += $"&snapshotUpperBound={snapshotUpperBound.Value}";
        }

        if (limit.HasValue)
        {
            query += $"&limit={limit.Value}";
        }

        using var response = await client.GetAsync($"/api/sync{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SyncResponse>())!;
    }

    private static void AssertSyncInvariants(SyncResponse response, long requestCursor)
    {
        var ids = response.Messages.Select(message => message.Id).ToArray();
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(ids.Distinct(), ids);
        Assert.All(ids, id => Assert.InRange(id, requestCursor + 1, response.NextCursor));
        Assert.InRange(response.NextCursor, requestCursor, response.SnapshotUpperBound);
        Assert.Equal(response.NextCursor < response.SnapshotUpperBound, response.HasMore);
        if (response.HasMore)
        {
            Assert.NotEmpty(ids);
            Assert.True(response.NextCursor > requestCursor);
        }

        if (response.SnapshotUpperBound > requestCursor)
        {
            Assert.True(response.NextCursor > requestCursor);
        }
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
