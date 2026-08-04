using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class SearchEndpointTests(RelayCoveWebApplicationFactory factory) :
    IClassFixture<RelayCoveWebApplicationFactory>,
    IAsyncLifetime
{
    private const string ExistingPassword = "a secure search endpoint test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Search_WhenUnauthenticatedOrQueryInvalid_ReturnsStableErrors()
    {
        using var anonymousClient = factory.CreateClient();
        using var unauthenticated = await anonymousClient.GetAsync("/api/search?keyword=test");
        await AssertErrorAsync(
            unauthenticated,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);

        var adminName = CreateUserName("search-validation-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(
            client,
            ConversationType.PublicChannel,
            $"Search validation {Guid.NewGuid():N}");
        var cases = new (string Suffix, string Key)[]
        {
            (string.Empty, "keyword"),
            ("?keyword=%20%20", "keyword"),
            ("?keyword=%01", "keyword"),
            ($"?keyword={Uri.EscapeDataString(new string('界', 65))}", "keyword"),
            ("?keyword=test&limit=0", "limit"),
            ("?keyword=test&limit=51", "limit"),
        };

        foreach (var invalid in cases)
        {
            using var response = await client.GetAsync($"/api/search{invalid.Suffix}");
            await AssertValidationErrorAsync(response, invalid.Key);
        }

        using var emptyId = await client.GetAsync(
            $"/api/search?keyword=test&conversationId={Guid.Empty:D}");
        await AssertErrorAsync(
            emptyId,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);

        Assert.NotEqual(Guid.Empty, conversation.Id);
    }

    [Fact]
    public async Task Search_InVisibleConversation_MatchesUnicodeLiteralsAndBoundAttachmentsOnce()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var adminName = $"search-content-{suffix}";
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(
            client,
            ConversationType.PublicChannel,
            $"Search content {suffix}");

        var chinese = await SendTextAsync(client, conversation.Id, $"开始需求确认结束-{suffix}");
        await AssertSingleMessageAsync(client, conversation.Id, "需求确认", chinese.Id);
        await AssertSingleMessageAsync(client, conversation.Id, "求确", chinese.Id);

        var percent = await SendTextAsync(client, conversation.Id, $"percent-{suffix}-%-literal");
        await SendTextAsync(client, conversation.Id, $"percent-{suffix}-wildcard-decoy");
        await AssertSingleMessageAsync(client, conversation.Id, "%", percent.Id);
        var underscore = await SendTextAsync(client, conversation.Id, $"underscore-{suffix}_literal");
        await SendTextAsync(client, conversation.Id, $"underscore-{suffix}Xliteral");
        await AssertSingleMessageAsync(client, conversation.Id, "_", underscore.Id);
        var slash = await SendTextAsync(client, conversation.Id, $"slash-{suffix}\\literal");
        await AssertSingleMessageAsync(client, conversation.Id, "\\", slash.Id);

        var fileKeyword = $"attach-{suffix}";
        var orphan = await UploadAsync(client, [1], $"orphan-{fileKeyword}.bin");
        var contentNullAttachment = await UploadAsync(client, [2], $"bound-{fileKeyword}.bin");
        var contentNullMessage = await SendAttachmentMessageAsync(
            client,
            conversation.Id,
            content: null,
            [contentNullAttachment.Id]);
        var first = await UploadAsync(client, [3], $"first-{fileKeyword}.bin");
        var second = await UploadAsync(client, [4], $"second-{fileKeyword}.bin");
        var combined = await SendAttachmentMessageAsync(
            client,
            conversation.Id,
            $"正文也包含 {fileKeyword}",
            [first.Id, second.Id]);

        var attachmentResults = await SearchAsync(client, fileKeyword, conversation.Id);
        Assert.False(attachmentResults.HasMore);
        Assert.Equal(
            new[] { combined.Id, contentNullMessage.Id },
            attachmentResults.Results.Select(result => result.MessageId));
        Assert.DoesNotContain(attachmentResults.Results, result =>
            string.Equals(result.MatchedAttachmentFileName, orphan.OriginalFileName, StringComparison.Ordinal));
        var combinedResult = Assert.Single(
            attachmentResults.Results,
            result => result.MessageId == combined.Id);
        Assert.Equal(
            new[] { first, second }
                .OrderBy(attachment => attachment.Id.ToString("D"), StringComparer.Ordinal)
                .First()
                .OriginalFileName,
            combinedResult.MatchedAttachmentFileName);
        var contentNullResult = Assert.Single(
            attachmentResults.Results,
            result => result.MessageId == contentNullMessage.Id);
        Assert.Equal(string.Empty, contentNullResult.Snippet);
    }

    [Fact]
    public async Task Search_GlobalAndScoped_EnforcesCurrentConversationAccess()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var creatorName = $"search-owner-{suffix}";
        var memberName = $"search-member-{suffix}";
        var peerName = $"search-peer-{suffix}";
        var outsiderAdminName = $"search-outsider-{suffix}";
        await factory.CreateUserAsync(creatorName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        var peerId = await factory.CreateUserAsync(peerName, ExistingPassword);
        await factory.CreateUserAsync(outsiderAdminName, ExistingPassword, isAdmin: true);
        using var creatorClient = await CreateAuthenticatedClientAsync(creatorName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        using var peerClient = await CreateAuthenticatedClientAsync(peerName);
        using var outsiderAdminClient = await CreateAuthenticatedClientAsync(outsiderAdminName);
        var publicConversation = await CreateChannelAsync(
            creatorClient,
            ConversationType.PublicChannel,
            $"Search public {suffix}");
        var privateConversation = await CreateChannelAsync(
            creatorClient,
            ConversationType.PrivateChannel,
            $"Search private {suffix}");
        await UpsertMemberAsync(creatorClient, privateConversation.Id, memberId);
        var directConversation = await CreateDirectAsync(memberClient, peerId);
        var keyword = $"permission-{suffix}";
        var publicMessage = await SendTextAsync(creatorClient, publicConversation.Id, keyword);
        var privateMessage = await SendTextAsync(creatorClient, privateConversation.Id, keyword);
        var directMessage = await SendTextAsync(memberClient, directConversation.Id, keyword);

        var memberGlobal = await SearchAsync(memberClient, keyword);
        Assert.Equal(
            new[] { directMessage.Id, privateMessage.Id, publicMessage.Id },
            memberGlobal.Results.Select(result => result.MessageId));
        var outsiderGlobal = await SearchAsync(outsiderAdminClient, keyword);
        Assert.Equal([publicMessage.Id], outsiderGlobal.Results.Select(result => result.MessageId));
        var peerGlobal = await SearchAsync(peerClient, keyword);
        Assert.Equal(
            new[] { directMessage.Id, publicMessage.Id },
            peerGlobal.Results.Select(result => result.MessageId));

        using (var denied = await outsiderAdminClient.GetAsync(SearchUrl(keyword, privateConversation.Id)))
        {
            await AssertErrorAsync(
                denied,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        using (var unknown = await memberClient.GetAsync(SearchUrl(keyword, Guid.NewGuid())))
        {
            await AssertErrorAsync(
                unknown,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        var noMatch = await SearchAsync(memberClient, $"missing-{Guid.NewGuid():N}", publicConversation.Id);
        Assert.Empty(noMatch.Results);
        Assert.False(noMatch.HasMore);

        using (var revoke = await creatorClient.DeleteAsync(
                   $"/api/conversations/{privateConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using (var revoked = await memberClient.GetAsync(SearchUrl(keyword, privateConversation.Id)))
        {
            await AssertErrorAsync(
                revoked,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        var deletedConversation = await CreateChannelAsync(
            creatorClient,
            ConversationType.PrivateChannel,
            $"Search deleted {suffix}");
        await MarkConversationDeletedAsync(deletedConversation.Id);
        using var deleted = await creatorClient.GetAsync(SearchUrl(keyword, deletedConversation.Id));
        await AssertErrorAsync(
            deleted,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);
    }

    [Fact]
    public async Task Search_WhenLimited_ReturnsUniqueMessagesNewestFirstAndSafeMetadata()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var ownerName = $"search-limit-{suffix}";
        var peerName = $"search-limit-peer-{suffix}";
        var ownerId = await factory.CreateUserAsync(ownerName, ExistingPassword, isAdmin: true);
        var peerId = await factory.CreateUserAsync(peerName, ExistingPassword);
        using var ownerClient = await CreateAuthenticatedClientAsync(ownerName);
        var publicConversation = await CreateChannelAsync(
            ownerClient,
            ConversationType.PublicChannel,
            $"Search limit {suffix}");
        var keyword = $"bounded-{suffix}";
        var insertedIds = await InsertMessagesAsync(ownerId, publicConversation.Id, keyword, 51);

        var one = await SearchAsync(ownerClient, keyword, publicConversation.Id, limit: 1);
        Assert.True(one.HasMore);
        Assert.Equal([insertedIds[^1]], one.Results.Select(result => result.MessageId));
        var fifty = await SearchAsync(ownerClient, keyword, publicConversation.Id, limit: 50);
        Assert.True(fifty.HasMore);
        Assert.Equal(
            insertedIds.OrderDescending().Take(50),
            fifty.Results.Select(result => result.MessageId));
        Assert.Equal(50, fifty.Results.Select(result => result.MessageId).Distinct().Count());

        var direct = await CreateDirectAsync(ownerClient, peerId);
        var directKeyword = $"direct-name-{suffix}";
        var file = await UploadAsync(ownerClient, [5], $"sensitive-{directKeyword}.txt");
        var directMessage = await SendAttachmentMessageAsync(
            ownerClient,
            direct.Id,
            directKeyword,
            [file.Id]);
        var logOffset = factory.LogMessages.Count;
        var directResult = Assert.Single((await SearchAsync(ownerClient, directKeyword)).Results);
        Assert.Equal(directMessage.Id, directResult.MessageId);
        Assert.Equal(peerName, directResult.ConversationName);
        Assert.Equal(file.OriginalFileName, directResult.MatchedAttachmentFileName);

        var logs = string.Join(Environment.NewLine, factory.LogMessages.Skip(logOffset));
        foreach (var secret in new[]
                 {
                     directKeyword,
                     directResult.Snippet,
                     file.OriginalFileName,
                     ownerName,
                     peerName,
                     ownerId.ToString("D"),
                     peerId.ToString("D"),
                     direct.Id.ToString("D"),
                 })
        {
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Search_WhenActorDisabled_ReturnsAuthenticationRequired()
    {
        var userName = CreateUserName("search-disabled");
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(userName);
        await factory.SetUserDisabledAsync(userId, true);

        using var response = await client.GetAsync("/api/search?keyword=test");
        await AssertErrorAsync(
            response,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task Search_WhenSubjectExceedsRateLimit_ReturnsStableErrorWithoutThrottlingAnotherSubject()
    {
        var firstUserName = CreateUserName("search-rate-first");
        var secondUserName = CreateUserName("search-rate-second");
        await factory.CreateUserAsync(firstUserName, ExistingPassword);
        await factory.CreateUserAsync(secondUserName, ExistingPassword);
        using var firstClient = await CreateAuthenticatedClientAsync(firstUserName);
        using var secondClient = await CreateAuthenticatedClientAsync(secondUserName);

        for (var requestNumber = 0; requestNumber < 30; requestNumber++)
        {
            using var allowed = await firstClient.GetAsync("/api/search?keyword=rate-limit-miss");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await firstClient.GetAsync("/api/search?keyword=rate-limit-miss");
        await AssertErrorAsync(
            rejected,
            HttpStatusCode.TooManyRequests,
            ApiErrorCodes.RateLimitExceeded);

        using var independentSubject = await secondClient.GetAsync("/api/search?keyword=rate-limit-miss");
        Assert.Equal(HttpStatusCode.OK, independentSubject.StatusCode);
    }

    private async Task MarkConversationDeletedAsync(Guid conversationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var conversation = await dbContext.Conversations.SingleAsync(item => item.Id == conversationId);
        conversation.MarkDeleted(conversation.UpdatedAt);
        await dbContext.SaveChangesAsync();
    }

    private async Task<long[]> InsertMessagesAsync(
        Guid senderId,
        Guid conversationId,
        string keyword,
        int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var createdAt = DateTime.UtcNow;
        var messages = Enumerable.Range(0, count)
            .Select(index => new Message(
                Guid.NewGuid(),
                conversationId,
                senderId,
                MessageType.Text,
                $"{keyword}-{index}",
                replyToMessageId: null,
                createdAt.AddMilliseconds(index)))
            .ToArray();
        dbContext.Messages.AddRange(messages);
        await dbContext.SaveChangesAsync();
        return messages.Select(message => message.Id).ToArray();
    }

    private async Task AssertSingleMessageAsync(
        HttpClient client,
        Guid conversationId,
        string keyword,
        long expectedMessageId)
    {
        var response = await SearchAsync(client, keyword, conversationId);
        var result = Assert.Single(response.Results);
        Assert.Equal(expectedMessageId, result.MessageId);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string userName)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "search-endpoint-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
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

    private static async Task<ConversationDto> CreateDirectAsync(
        HttpClient client,
        Guid participantUserId)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(
                ConversationType.Direct,
                ParticipantUserId: participantUserId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task UpsertMemberAsync(
        HttpClient client,
        Guid conversationId,
        Guid userId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/members",
            new UpsertConversationMemberRequest(userId, ConversationMemberRole.Member));
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Created });
    }

    private static async Task<MessageDto> SendTextAsync(
        HttpClient client,
        Guid conversationId,
        string content) =>
        await SendMessageAsync(
            client,
            new SendMessageRequest(
                Guid.NewGuid(),
                conversationId,
                MessageType.Text,
                content,
                ReplyToMessageId: null,
                AttachmentIds: [],
                MentionUserIds: []));

    private static async Task<MessageDto> SendAttachmentMessageAsync(
        HttpClient client,
        Guid conversationId,
        string? content,
        IReadOnlyList<Guid> attachmentIds) =>
        await SendMessageAsync(
            client,
            new SendMessageRequest(
                Guid.NewGuid(),
                conversationId,
                MessageType.File,
                content,
                ReplyToMessageId: null,
                AttachmentIds: attachmentIds,
                MentionUserIds: []));

    private static async Task<MessageDto> SendMessageAsync(
        HttpClient client,
        SendMessageRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/messages", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>())!;
    }

    private static async Task<AttachmentDto> UploadAsync(
        HttpClient client,
        byte[] bytes,
        string fileName)
    {
        using var form = new MultipartFormDataContent($"relaycove-{Guid.NewGuid():N}");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(file, "file", fileName);
        using var response = await client.PostAsync("/api/attachments", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttachmentDto>())!;
    }

    private async Task<SearchResponse> SearchAsync(
        HttpClient client,
        string keyword,
        Guid? conversationId = null,
        int? limit = null)
    {
        using var response = await client.GetAsync(SearchUrl(keyword, conversationId, limit));
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 but received {(int)response.StatusCode}. " +
            string.Join(Environment.NewLine, factory.LogMessages.TakeLast(20)));
        return (await response.Content.ReadFromJsonAsync<SearchResponse>())!;
    }

    private static string SearchUrl(
        string keyword,
        Guid? conversationId = null,
        int? limit = null)
    {
        var query = new List<string> { $"keyword={Uri.EscapeDataString(keyword)}" };
        if (conversationId.HasValue)
        {
            query.Add($"conversationId={conversationId.Value:D}");
        }

        if (limit.HasValue)
        {
            query.Add($"limit={limit.Value}");
        }

        return $"/api/search?{string.Join('&', query)}";
    }

    private static async Task AssertValidationErrorAsync(
        HttpResponseMessage response,
        string expectedKey)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(ApiErrorCodes.ValidationFailed, error.Code);
        Assert.Equal([expectedKey], error.Details!.Keys);
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(expectedCode, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }

    private static string CreateUserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
