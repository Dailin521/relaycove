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

public sealed class MessageAroundEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string ExistingPassword = "a secure around test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AroundEndpoint_WhenUnauthenticatedOrInputInvalid_ReturnsStableErrors()
    {
        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.GetAsync(
                   $"/api/conversations/{Guid.NewGuid():D}/messages/around/1"))
        {
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
        }

        var adminName = CreateUserName("around-validation-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Around validation");

        foreach (var requestPath in new[]
                 {
                     $"/api/conversations/{conversation.Id:D}/messages/around/0",
                     $"/api/conversations/{conversation.Id:D}/messages/around/-1",
                     $"/api/conversations/{conversation.Id:D}/messages/around/1?before=-1",
                     $"/api/conversations/{conversation.Id:D}/messages/around/1?before=101",
                     $"/api/conversations/{conversation.Id:D}/messages/around/1?after=-1",
                     $"/api/conversations/{conversation.Id:D}/messages/around/1?after=101",
                 })
        {
            using var response = await client.GetAsync(requestPath);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task Around_WhenWindowsVary_ReturnsNearestOrderedMessagesTargetAndAccurateMoreFlags()
    {
        var adminName = CreateUserName("around-window-admin");
        var firstMentionName = CreateUserName("around-mention-first");
        var secondMentionName = CreateUserName("around-mention-second");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var firstMentionId = await factory.CreateUserAsync(firstMentionName, ExistingPassword);
        var secondMentionId = await factory.CreateUserAsync(secondMentionName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Around windows");
        var messages = new List<MessageDto>();
        for (var index = 0; index < 7; index++)
        {
            var request = CreateSendRequest(conversation.Id, $"around message {index + 1}");
            if (index == 3)
            {
                request = request with { MentionUserIds = [secondMentionId, firstMentionId, adminId] };
            }

            messages.Add(await SendAsync(client, request));
        }

        var target = messages[3];
        var aroundLogOffset = factory.LogMessages.Count;
        var defaultWindow = await GetAroundAsync(client, conversation.Id, target.Id);
        Assert.Equal(messages.Select(message => message.Id), defaultWindow.Messages.Select(message => message.Id));
        Assert.False(defaultWindow.HasMoreBefore);
        Assert.False(defaultWindow.HasMoreAfter);

        var bounded = await GetAroundAsync(client, conversation.Id, target.Id, before: 2, after: 1);
        Assert.Equal(target.Id, bounded.TargetMessageId);
        Assert.Equal(
            messages.Skip(1).Take(4).Select(message => message.Id),
            bounded.Messages.Select(message => message.Id));
        Assert.True(bounded.HasMoreBefore);
        Assert.True(bounded.HasMoreAfter);
        Assert.Equal(
            new[] { adminId, firstMentionId, secondMentionId }.Order(),
            Assert.Single(bounded.Messages, message => message.Id == target.Id).MentionUserIds);
        Assert.Equal(
            bounded.Messages.Select(message => message.Id).Order(),
            bounded.Messages.Select(message => message.Id));
        Assert.Equal(1, bounded.Messages.Count(message => message.Id == target.Id));

        var targetOnly = await GetAroundAsync(client, conversation.Id, target.Id, before: 0, after: 0);
        Assert.Equal([target.Id], targetOnly.Messages.Select(message => message.Id));
        Assert.True(targetOnly.HasMoreBefore);
        Assert.True(targetOnly.HasMoreAfter);

        var first = await GetAroundAsync(client, conversation.Id, messages[0].Id, before: 2, after: 0);
        Assert.Equal([messages[0].Id], first.Messages.Select(message => message.Id));
        Assert.False(first.HasMoreBefore);
        Assert.True(first.HasMoreAfter);

        var last = await GetAroundAsync(client, conversation.Id, messages[^1].Id, before: 0, after: 2);
        Assert.Equal([messages[^1].Id], last.Messages.Select(message => message.Id));
        Assert.True(last.HasMoreBefore);
        Assert.False(last.HasMoreAfter);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MessageQueryService>();
        var logOffset = factory.LogMessages.Count;
        var serviceResult = await service.GetAroundAsync(
            adminId,
            conversation.Id,
            target.Id,
            before: 2,
            after: 1,
            CancellationToken.None);
        Assert.Equal(MessageOperationStatus.Success, serviceResult.Status);
        var selectCommands = factory.LogMessages
            .Skip(logOffset)
            .Where(message =>
                message.Contains("Executed DbCommand", StringComparison.Ordinal) &&
                message.Contains("SELECT", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, selectCommands.Length);
        Assert.DoesNotContain(
            factory.LogMessages.Skip(aroundLogOffset),
            message => message.Contains(target.Content!, StringComparison.Ordinal));
        Assert.DoesNotContain(
            factory.LogMessages.Skip(aroundLogOffset),
            message => message.Contains(adminName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Around_WhenTargetOrAccessChanges_FailsClosedWithoutLeakingTargetState()
    {
        var creatorName = CreateUserName("around-access-creator");
        var memberName = CreateUserName("around-access-member");
        var outsiderAdminName = CreateUserName("around-access-outsider-admin");
        var disabledName = CreateUserName("around-access-disabled");
        await factory.CreateUserAsync(creatorName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        await factory.CreateUserAsync(outsiderAdminName, ExistingPassword, isAdmin: true);
        var disabledId = await factory.CreateUserAsync(disabledName, ExistingPassword);
        using var creatorClient = await CreateAuthenticatedClientAsync(creatorName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        using var outsiderAdminClient = await CreateAuthenticatedClientAsync(outsiderAdminName);
        using var disabledClient = await CreateAuthenticatedClientAsync(disabledName);
        var firstPublic = await CreateChannelAsync(
            creatorClient, ConversationType.PublicChannel, "Around access first");
        var secondPublic = await CreateChannelAsync(
            creatorClient, ConversationType.PublicChannel, "Around access second");
        var firstMessage = await SendAsync(creatorClient, CreateSendRequest(firstPublic.Id, "first secret"));
        var secondMessage = await SendAsync(creatorClient, CreateSendRequest(secondPublic.Id, "second secret"));

        foreach (var invalidTarget in new[] { secondMessage.Id, long.MaxValue })
        {
            using var invalidResponse = await memberClient.GetAsync(
                $"/api/conversations/{firstPublic.Id:D}/messages/around/{invalidTarget}");
            await AssertErrorAsync(invalidResponse, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        using (var unknownResponse = await memberClient.GetAsync(
                   $"/api/conversations/{Guid.NewGuid():D}/messages/around/{firstMessage.Id}"))
        {
            await AssertErrorAsync(
                unknownResponse,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }

        var privateConversation = await CreateChannelAsync(
            creatorClient, ConversationType.PrivateChannel, "Around private");
        var privateMessage = await SendAsync(
            creatorClient,
            CreateSendRequest(privateConversation.Id, "private-around-secret"));
        foreach (var inaccessibleTarget in new[] { privateMessage.Id, long.MaxValue })
        {
            using var denied = await outsiderAdminClient.GetAsync(
                $"/api/conversations/{privateConversation.Id:D}/messages/around/{inaccessibleTarget}");
            await AssertErrorAsync(denied, HttpStatusCode.Forbidden, ApiErrorCodes.ConversationAccessRevoked);
        }

        await UpsertMemberAsync(creatorClient, privateConversation.Id, memberId);
        Assert.Equal(
            privateMessage.Id,
            (await GetAroundAsync(memberClient, privateConversation.Id, privateMessage.Id)).TargetMessageId);
        using (var revoke = await creatorClient.DeleteAsync(
                   $"/api/conversations/{privateConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using (var revoked = await memberClient.GetAsync(
                   $"/api/conversations/{privateConversation.Id:D}/messages/around/{privateMessage.Id}"))
        {
            await AssertErrorAsync(revoked, HttpStatusCode.Forbidden, ApiErrorCodes.ConversationAccessRevoked);
        }

        var directConversation = await CreateDirectAsync(creatorClient, memberId);
        var directMessage = await SendAsync(
            creatorClient,
            CreateSendRequest(directConversation.Id, "direct around"));
        Assert.Equal(
            directMessage.Id,
            (await GetAroundAsync(memberClient, directConversation.Id, directMessage.Id)).TargetMessageId);

        await using (var deleteScope = factory.Services.CreateAsyncScope())
        {
            var dbContext = deleteScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var stored = await dbContext.Conversations.SingleAsync(
                conversation => conversation.Id == privateConversation.Id);
            stored.MarkDeleted(stored.UpdatedAt);
            await dbContext.SaveChangesAsync();
        }

        using (var deleted = await creatorClient.GetAsync(
                   $"/api/conversations/{privateConversation.Id:D}/messages/around/{privateMessage.Id}"))
        {
            await AssertErrorAsync(deleted, HttpStatusCode.Forbidden, ApiErrorCodes.ConversationAccessRevoked);
        }

        await factory.SetUserDisabledAsync(disabledId, true);
        using var disabled = await disabledClient.GetAsync(
            $"/api/conversations/{firstPublic.Id:D}/messages/around/{firstMessage.Id}");
        await AssertErrorAsync(disabled, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string userName)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "around-test", "1.0.0"));
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

    private static async Task<ConversationDto> CreateDirectAsync(
        HttpClient client,
        Guid participantUserId)
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

    private static async Task<MessageAroundResponse> GetAroundAsync(
        HttpClient client,
        Guid conversationId,
        long messageId,
        int? before = null,
        int? after = null)
    {
        var query = before.HasValue || after.HasValue
            ? $"?before={before ?? MessageRequestValidator.DefaultAroundSideCount}" +
              $"&after={after ?? MessageRequestValidator.DefaultAroundSideCount}"
            : string.Empty;
        using var response = await client.GetAsync(
            $"/api/conversations/{conversationId:D}/messages/around/{messageId}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageAroundResponse>())!;
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
