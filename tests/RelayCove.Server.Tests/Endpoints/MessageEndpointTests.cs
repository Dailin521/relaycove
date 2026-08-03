using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
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

public sealed class MessageEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string ExistingPassword = "a secure message test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MessageEndpoints_WhenUnauthenticatedOrPayloadUnsupported_ReturnStableErrors()
    {
        using (var anonymous = factory.CreateClient())
        {
            using var sendResponse = await anonymous.PostAsJsonAsync(
                "/api/messages",
                CreateSendRequest(Guid.NewGuid(), "text"));
            await AssertErrorAsync(sendResponse, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);

            using var historyResponse = await anonymous.GetAsync($"/api/conversations/{Guid.NewGuid():D}/messages");
            await AssertErrorAsync(historyResponse, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
        }

        var adminName = CreateUserName("validation-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Validation");

        using (var emptyText = await client.PostAsJsonAsync(
                   "/api/messages",
                   CreateSendRequest(conversation.Id, " \t\r\n")))
        {
            await AssertErrorAsync(emptyText, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        foreach (var invalidContent in new[]
                 {
                     "contains\0control",
                     new string('x', 4_001),
                 })
        {
            using var invalidText = await client.PostAsJsonAsync(
                "/api/messages",
                CreateSendRequest(conversation.Id, invalidContent));
            await AssertErrorAsync(invalidText, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        using (var attachment = await client.PostAsJsonAsync(
                   "/api/messages",
                   CreateSendRequest(conversation.Id, "text") with { AttachmentIds = [Guid.NewGuid()] }))
        {
            await AssertErrorAsync(attachment, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        var duplicateMention = Guid.NewGuid();
        using (var mentions = await client.PostAsJsonAsync(
                   "/api/messages",
                   CreateSendRequest(conversation.Id, "text") with
                   {
                       MentionUserIds = [duplicateMention, duplicateMention],
                   }))
        {
            await AssertErrorAsync(mentions, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        using (var unsupported = await client.PostAsJsonAsync(
                   "/api/messages",
                   CreateSendRequest(conversation.Id, null) with { Type = MessageType.Image }))
        {
            await AssertErrorAsync(unsupported, HttpStatusCode.Conflict, ApiErrorCodes.MessageTypeUnsupported);
        }

        using (var invalidHistory = await client.GetAsync(
                   $"/api/conversations/{conversation.Id:D}/messages?beforeMessageId=0&limit=101"))
        {
            await AssertErrorAsync(invalidHistory, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task Send_WhenReplayedSequentiallyConcurrentlyOrAfterRevocation_EnforcesIdempotencyBoundary()
    {
        var adminName = CreateUserName("idempotency-admin");
        var memberName = CreateUserName("idempotency-member");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        var conversation = await CreateChannelAsync(adminClient, ConversationType.PrivateChannel, "Idempotency");
        await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        const string secret = "exact secret payload 5b7f2c";
        var request = CreateSendRequest(conversation.Id, secret) with
        {
            MentionUserIds = [memberId, adminId],
        };
        var logOffset = factory.LogMessages.Count;

        using var createdResponse = await memberClient.PostAsJsonAsync("/api/messages", request);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<MessageDto>())!;
        Assert.Equal(secret, created.Content);
        Assert.Equal($"/api/conversations/{conversation.Id:D}/messages/{created.Id}",
            createdResponse.Headers.Location!.OriginalString);

        using var replayResponse = await memberClient.PostAsJsonAsync(
            "/api/messages",
            request with { MentionUserIds = [adminId, memberId] });
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(created.Id, (await replayResponse.Content.ReadFromJsonAsync<MessageDto>())!.Id);

        using var conflictResponse = await memberClient.PostAsJsonAsync(
            "/api/messages",
            request with { Content = "different payload" });
        await AssertErrorAsync(conflictResponse, HttpStatusCode.Conflict, ApiErrorCodes.IdempotencyKeyReuse);

        var concurrentRequest = request with { ClientMessageId = Guid.NewGuid(), Content = "concurrent" };
        var firstTask = memberClient.PostAsJsonAsync("/api/messages", concurrentRequest);
        var secondTask = memberClient.PostAsJsonAsync("/api/messages", concurrentRequest);
        using var firstResponse = await firstTask;
        using var secondResponse = await secondTask;
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Created],
            new[] { firstResponse.StatusCode, secondResponse.StatusCode }.Order());
        Assert.Equal(
            (await firstResponse.Content.ReadFromJsonAsync<MessageDto>())!.Id,
            (await secondResponse.Content.ReadFromJsonAsync<MessageDto>())!.Id);

        using (var removeResponse = await adminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);
        }

        using var revokedReplay = await memberClient.PostAsJsonAsync("/api/messages", request);
        await AssertErrorAsync(
            revokedReplay,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);
        using var revokedFirstSend = await memberClient.PostAsJsonAsync(
            "/api/messages",
            CreateSendRequest(conversation.Id, "new key after revocation"));
        await AssertErrorAsync(
            revokedFirstSend,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(2, await dbContext.Messages.CountAsync(message => message.ConversationId == conversation.Id));
        Assert.Equal(1, await dbContext.Messages.CountAsync(message =>
            message.SenderId == memberId && message.ClientMessageId == concurrentRequest.ClientMessageId));
        Assert.DoesNotContain(
            factory.LogMessages.Skip(logOffset),
            message => message.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_WhenReplyOrMentionsAreUsed_RequiresSameConversationAndCurrentContentAccess()
    {
        var adminName = CreateUserName("relations-admin");
        var memberName = CreateUserName("relations-member");
        var outsiderName = CreateUserName("relations-outsider");
        var disabledName = CreateUserName("relations-disabled");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        var outsiderId = await factory.CreateUserAsync(outsiderName, ExistingPassword);
        var disabledId = await factory.CreateUserAsync(disabledName, ExistingPassword, isDisabled: true);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        var firstConversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Relations A");
        var secondConversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Relations B");
        await UpsertMemberAsync(adminClient, firstConversation.Id, memberId);

        var root = await SendAsync(adminClient, CreateSendRequest(firstConversation.Id, "root"), HttpStatusCode.Created);
        var otherRoot = await SendAsync(adminClient, CreateSendRequest(secondConversation.Id, "other"), HttpStatusCode.Created);
        var validRequest = CreateSendRequest(firstConversation.Id, "reply with mention") with
        {
            ReplyToMessageId = root.Id,
            MentionUserIds = [memberId],
        };
        var valid = await SendAsync(memberClient, validRequest, HttpStatusCode.Created);
        Assert.Equal(root.Id, valid.ReplyToMessageId);
        Assert.Equal([memberId], valid.MentionUserIds);

        using (var crossConversationReply = await memberClient.PostAsJsonAsync(
                   "/api/messages",
                   CreateSendRequest(firstConversation.Id, "bad reply") with
                   {
                       ReplyToMessageId = otherRoot.Id,
                   }))
        {
            await AssertErrorAsync(crossConversationReply, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        foreach (var inaccessibleMention in new[] { outsiderId, disabledId })
        {
            using var mentionResponse = await memberClient.PostAsJsonAsync(
                "/api/messages",
                CreateSendRequest(firstConversation.Id, "bad mention") with
                {
                    MentionUserIds = [inaccessibleMention],
                });
            await AssertErrorAsync(mentionResponse, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task HistoryAndConversationList_WhenMessagesExist_UseExclusiveKeysetAndAuthoritativeAggregates()
    {
        var adminName = CreateUserName("history-admin");
        var readerName = CreateUserName("history-reader");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var readerId = await factory.CreateUserAsync(readerName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var readerClient = await CreateAuthenticatedClientAsync(readerName);
        var conversation = await CreateChannelAsync(adminClient, ConversationType.PublicChannel, "History");
        var sent = new List<MessageDto>();
        for (var index = 1; index <= 5; index++)
        {
            var request = CreateSendRequest(conversation.Id, $"message {index}");
            if (index == 5)
            {
                request = request with { MentionUserIds = [readerId] };
            }

            sent.Add(await SendAsync(adminClient, request, HttpStatusCode.Created));
        }

        var firstPage = await GetHistoryAsync(readerClient, conversation.Id, limit: 2);
        Assert.True(firstPage.HasMore);
        Assert.Equal(sent[^2..].Select(message => message.Id), firstPage.Messages.Select(message => message.Id));
        Assert.Equal([readerId], firstPage.Messages[^1].MentionUserIds);
        Assert.Equal(firstPage.Messages[0].Id, firstPage.NextBeforeMessageId);
        var secondPage = await GetHistoryAsync(
            readerClient, conversation.Id, firstPage.NextBeforeMessageId, limit: 2);
        Assert.True(secondPage.HasMore);
        Assert.Equal(sent.Skip(1).Take(2).Select(message => message.Id),
            secondPage.Messages.Select(message => message.Id));
        var thirdPage = await GetHistoryAsync(
            readerClient, conversation.Id, secondPage.NextBeforeMessageId, limit: 2);
        Assert.False(thirdPage.HasMore);
        Assert.Null(thirdPage.NextBeforeMessageId);
        Assert.Equal(sent.Take(1).Select(message => message.Id), thirdPage.Messages.Select(message => message.Id));
        Assert.Equal(
            sent.Select(message => message.Id).Order(),
            firstPage.Messages.Concat(secondPage.Messages).Concat(thirdPage.Messages)
                .Select(message => message.Id)
                .Order());

        var readerView = Assert.Single(
            (await GetConversationListAsync(readerClient)).Conversations,
            candidate => candidate.Id == conversation.Id);
        Assert.Equal(sent[^1].Id, readerView.LastMessageId);
        Assert.Equal(0, readerView.LastReadMessageId);
        Assert.Equal(5, readerView.UnreadCount);
        var senderView = Assert.Single(
            (await GetConversationListAsync(adminClient)).Conversations,
            candidate => candidate.Id == conversation.Id);
        Assert.Equal(sent[^1].Id, senderView.LastMessageId);
        Assert.Equal(0, senderView.UnreadCount);

        var emptyConversation = await CreateChannelAsync(
            adminClient, ConversationType.PublicChannel, "Empty history");
        var emptyHistory = await GetHistoryAsync(readerClient, emptyConversation.Id);
        Assert.Empty(emptyHistory.Messages);
        Assert.False(emptyHistory.HasMore);
    }

    [Fact]
    public async Task PrivateMemberJoin_WhenMessagesAlreadyExist_UsesCurrentMaxAsUnreadWatermark()
    {
        var adminName = CreateUserName("watermark-admin");
        var memberName = CreateUserName("watermark-member");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        var conversation = await CreateChannelAsync(
            adminClient, ConversationType.PrivateChannel, "Watermark");
        var oldFirst = await SendAsync(
            adminClient, CreateSendRequest(conversation.Id, "old 1"), HttpStatusCode.Created);
        var oldLast = await SendAsync(
            adminClient, CreateSendRequest(conversation.Id, "old 2"), HttpStatusCode.Created);

        using (var deniedHistory = await memberClient.GetAsync(
                   $"/api/conversations/{conversation.Id:D}/messages"))
        {
            await AssertErrorAsync(
                deniedHistory,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        var joined = await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        Assert.Equal(oldLast.Id, joined.LastReadMessageId);
        Assert.Equal(
            new[] { oldFirst.Id, oldLast.Id },
            (await GetHistoryAsync(memberClient, conversation.Id)).Messages.Select(message => message.Id));
        var joinedView = Assert.Single(
            (await GetConversationListAsync(memberClient)).Conversations,
            candidate => candidate.Id == conversation.Id);
        Assert.Equal(oldLast.Id, joinedView.LastReadMessageId);
        Assert.Equal(0, joinedView.UnreadCount);

        var newMessage = await SendAsync(
            adminClient, CreateSendRequest(conversation.Id, "new"), HttpStatusCode.Created);
        var unreadView = Assert.Single(
            (await GetConversationListAsync(memberClient)).Conversations,
            candidate => candidate.Id == conversation.Id);
        Assert.Equal(newMessage.Id, unreadView.LastMessageId);
        Assert.Equal(1, unreadView.UnreadCount);

        using (var removeResponse = await adminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);
        }

        var whileRemoved = await SendAsync(
            adminClient, CreateSendRequest(conversation.Id, "while removed"), HttpStatusCode.Created);
        var rejoined = await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        Assert.Equal(whileRemoved.Id, rejoined.LastReadMessageId);
        var repeated = await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        Assert.Equal(whileRemoved.Id, repeated.LastReadMessageId);
    }

    [Fact]
    public async Task MessageWrite_WhenSqliteIsBusy_ReturnsStableServiceUnavailable()
    {
        using var busyFactory = new RelayCoveWebApplicationFactory(1_000, 1_000, databaseTimeoutSeconds: 1);
        await busyFactory.InitializeDatabaseAsync();
        var adminName = CreateUserName("message-busy-admin");
        await busyFactory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(busyFactory, adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Busy messages");
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
            "/api/messages",
            CreateSendRequest(conversation.Id, "busy"));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task MessageHistoryService_WhenRead_CombinesAccessAndPageInOneDatabaseCommand()
    {
        var adminName = CreateUserName("history-single-query");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(
            client, ConversationType.PrivateChannel, "Single query history");
        await SendAsync(client, CreateSendRequest(conversation.Id, "message"), HttpStatusCode.Created);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MessageQueryService>();
        var logOffset = factory.LogMessages.Count;

        var result = await service.GetHistoryAsync(
            adminId,
            conversation.Id,
            beforeMessageId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(MessageOperationStatus.Success, result.Status);
        Assert.Single(result.Value!.Messages);
        var databaseCommands = factory.LogMessages
            .Skip(logOffset)
            .Where(message =>
                message.Contains("Executed DbCommand", StringComparison.Ordinal) &&
                message.Contains("SELECT", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(databaseCommands);
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
            new LoginRequest(userName, ExistingPassword, "message-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static SendMessageRequest CreateSendRequest(Guid conversationId, string? content) => new(
        Guid.NewGuid(),
        conversationId,
        MessageType.Text,
        content,
        null,
        [],
        []);

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

    private static async Task<ConversationMemberDto> UpsertMemberAsync(
        HttpClient client,
        Guid conversationId,
        Guid userId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/members",
            new UpsertConversationMemberRequest(userId, ConversationMemberRole.Member));
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"Unexpected member upsert status: {response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<ConversationMemberDto>())!;
    }

    private static async Task<MessageDto> SendAsync(
        HttpClient client,
        SendMessageRequest request,
        HttpStatusCode expectedStatus)
    {
        using var response = await client.PostAsJsonAsync("/api/messages", request);
        Assert.Equal(expectedStatus, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>())!;
    }

    private static async Task<MessageHistoryResponse> GetHistoryAsync(
        HttpClient client,
        Guid conversationId,
        long? beforeMessageId = null,
        int? limit = null)
    {
        var query = new List<string>();
        if (beforeMessageId.HasValue)
        {
            query.Add($"beforeMessageId={beforeMessageId.Value}");
        }

        if (limit.HasValue)
        {
            query.Add($"limit={limit.Value}");
        }

        var suffix = query.Count == 0 ? string.Empty : $"?{string.Join('&', query)}";
        using var response = await client.GetAsync($"/api/conversations/{conversationId:D}/messages{suffix}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageHistoryResponse>())!;
    }

    private static async Task<ConversationListResponse> GetConversationListAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/conversations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationListResponse>())!;
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
