using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class MentionCandidateEndpointTests(
    RelayCoveWebApplicationFactory factory) :
    IClassFixture<RelayCoveWebApplicationFactory>,
    IAsyncLifetime
{
    private const string ExistingPassword = "a secure mention candidate test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MentionCandidates_WhenUnauthenticatedOrQueryInvalid_ReturnStableErrors()
    {
        using var anonymousClient = factory.CreateClient();
        using var unauthenticated = await anonymousClient.GetAsync(
            $"/api/conversations/{Guid.NewGuid():D}/mention-candidates?query=a");
        await AssertErrorAsync(
            unauthenticated,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);

        var adminName = CreateUserName("mention-validation-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            $"Mention validation {Guid.NewGuid():N}");

        using var missingQuery = await adminClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates");
        await AssertValidationErrorAsync(missingQuery, "query");

        using var invalidQuery = await adminClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates?query=alice%20smith");
        await AssertValidationErrorAsync(invalidQuery, "query");

        using var invalidLimit = await adminClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates?query=a&limit=51");
        await AssertValidationErrorAsync(invalidLimit, "limit");
    }

    [Fact]
    public async Task MentionCandidates_InPublicConversation_ReturnActiveLiteralPrefixInStablePages()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var adminName = $"mc-admin-{suffix}";
        var actorName = $"mc-actor-{suffix}";
        var prefix = $"mc_{suffix}";
        var firstName = $"{prefix}-a";
        var secondName = $"{prefix}-b";
        var wildcardDecoyName = $"mcx{suffix}-c";
        var disabledName = $"{prefix}-d";
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var actorId = await factory.CreateUserAsync(actorName, ExistingPassword);
        var firstId = await factory.CreateUserAsync(firstName, ExistingPassword);
        var secondId = await factory.CreateUserAsync(secondName, ExistingPassword);
        await factory.CreateUserAsync(wildcardDecoyName, ExistingPassword);
        await factory.CreateUserAsync(disabledName, ExistingPassword, isDisabled: true);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var actorClient = await CreateAuthenticatedClientAsync(actorName);
        var conversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            $"Mention public {suffix}");
        var encodedUpperPrefix = Uri.EscapeDataString(prefix.ToUpperInvariant());
        var logOffset = factory.LogMessages.Count;

        using var firstPageResponse = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            $"?query={encodedUpperPrefix}&limit=1");
        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        var firstPage = (await firstPageResponse.Content
            .ReadFromJsonAsync<MentionCandidateListResponse>())!;
        Assert.Equal(conversation.Id, firstPage.ConversationId);
        Assert.True(firstPage.HasMore);
        var firstCandidate = Assert.Single(firstPage.Candidates);
        Assert.Equal(firstId, firstCandidate.UserId);
        Assert.Equal(firstName, firstCandidate.UserName);
        Assert.Equal(firstName, firstCandidate.DisplayName);

        using var allResponse = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            $"?query={encodedUpperPrefix}&limit=50");
        Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);
        var all = (await allResponse.Content.ReadFromJsonAsync<MentionCandidateListResponse>())!;
        Assert.False(all.HasMore);
        Assert.Equal([firstId, secondId], all.Candidates.Select(candidate => candidate.UserId));
        Assert.Equal(
            [firstName, secondName],
            all.Candidates.Select(candidate => candidate.UserName));

        using var allMembersResponse = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates?query=&limit=50");
        Assert.Equal(HttpStatusCode.OK, allMembersResponse.StatusCode);
        var allMembers = (await allMembersResponse.Content
            .ReadFromJsonAsync<MentionCandidateListResponse>())!;
        Assert.Contains(allMembers.Candidates, candidate => candidate.UserId == actorId);
        Assert.Contains(allMembers.Candidates, candidate => candidate.UserId == firstId);
        Assert.Contains(allMembers.Candidates, candidate => candidate.UserId == secondId);
        Assert.DoesNotContain(
            allMembers.Candidates,
            candidate => string.Equals(candidate.UserName, disabledName, StringComparison.Ordinal));

        using var emptyResponse = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            "?query=no_such_candidate_prefix&limit=20");
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        var empty = (await emptyResponse.Content
            .ReadFromJsonAsync<MentionCandidateListResponse>())!;
        Assert.Empty(empty.Candidates);
        Assert.False(empty.HasMore);

        var logs = string.Join(Environment.NewLine, factory.LogMessages.Skip(logOffset));
        Assert.DoesNotContain(prefix, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstName, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondName, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(wildcardDecoyName, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(disabledName, logs, StringComparison.OrdinalIgnoreCase);

        await factory.SetUserDisabledAsync(actorId, isDisabled: true);
        using var disabledActorResponse = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            $"?query={encodedUpperPrefix}&limit=20");
        await AssertErrorAsync(
            disabledActorResponse,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task MentionCandidates_InPrivateConversation_ReturnMembersAndFailClosedAfterRevocation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var adminName = $"mp-admin-{suffix}";
        var actorName = $"mp-actor-{suffix}";
        var prefix = $"mp-{suffix}";
        var memberName = $"{prefix}-member";
        var outsiderName = $"{prefix}-outsider";
        var disabledName = $"{prefix}-disabled";
        var adminId = await factory.CreateUserAsync(
            adminName,
            ExistingPassword,
            isAdmin: true);
        var actorId = await factory.CreateUserAsync(actorName, ExistingPassword);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        await factory.CreateUserAsync(outsiderName, ExistingPassword);
        var disabledId = await factory.CreateUserAsync(disabledName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var actorClient = await CreateAuthenticatedClientAsync(actorName);
        using var outsiderClient = await CreateAuthenticatedClientAsync(outsiderName);
        var conversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            $"Mention private {suffix}");
        await UpsertMemberAsync(adminClient, conversation.Id, actorId);
        await UpsertMemberAsync(adminClient, conversation.Id, memberId);
        await UpsertMemberAsync(adminClient, conversation.Id, disabledId);
        await factory.SetUserDisabledAsync(disabledId, isDisabled: true);
        var encodedPrefix = Uri.EscapeDataString(prefix);

        using var response = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            $"?query={encodedPrefix}&limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<MentionCandidateListResponse>())!;
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(memberId, candidate.UserId);
        Assert.Equal(memberName, candidate.UserName);

        using var outsiderResponse = await outsiderClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            $"?query={encodedPrefix}");
        await AssertErrorAsync(
            outsiderResponse,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);

        using (var revoke = await adminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{actorId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }
        using var revokedResponse = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            $"?query={encodedPrefix}");
        await AssertErrorAsync(
            revokedResponse,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);

        using var unknownResponse = await adminClient.GetAsync(
            $"/api/conversations/{Guid.NewGuid():D}/mention-candidates?query={encodedPrefix}");
        await AssertErrorAsync(
            unknownResponse,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);
        Assert.NotEqual(Guid.Empty, adminId);
    }

    [Fact]
    public async Task MentionCandidates_InDirectConversation_ReturnOnlyActiveParticipants()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actorName = $"md-actor-{suffix}";
        var prefix = $"md-{suffix}";
        var participantName = $"{prefix}-participant";
        var outsiderName = $"{prefix}-outsider";
        var actorId = await factory.CreateUserAsync(actorName, ExistingPassword);
        var participantId = await factory.CreateUserAsync(participantName, ExistingPassword);
        await factory.CreateUserAsync(outsiderName, ExistingPassword);
        using var actorClient = await CreateAuthenticatedClientAsync(actorName);
        var conversation = await CreateDirectAsync(actorClient, participantId);

        using var response = await actorClient.GetAsync(
            $"/api/conversations/{conversation.Id:D}/mention-candidates" +
            $"?query={Uri.EscapeDataString(prefix)}&limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<MentionCandidateListResponse>())!;

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(participantId, candidate.UserId);
        Assert.Equal(participantName, candidate.UserName);
        Assert.DoesNotContain(result.Candidates, item => item.UserId == actorId);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string userName)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "mention-candidate-test", "1.0.0"));
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
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private static string CreateUserName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}";
}
