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

namespace RelayCove.Server.Tests.Endpoints;

public sealed class ConversationEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string ExistingPassword = "a secure conversation test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConversationEndpoints_WhenUnauthenticated_ReturnStableAuthenticationError()
    {
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync("/api/conversations");
        await AssertErrorAsync(
            listResponse,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);

        using var createResponse = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.PublicChannel, "General"));
        await AssertErrorAsync(
            createResponse,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task ChannelCreation_WhenActorIsCurrentAdministrator_CreatesVisibleAuthoritativeChannels()
    {
        var adminUserName = CreateUserName("channel-admin");
        var normalUserName = CreateUserName("channel-normal");
        var adminId = await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        await factory.CreateUserAsync(normalUserName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(adminUserName);
        using var normalClient = await CreateAuthenticatedClientAsync(normalUserName);

        using var deniedResponse = await normalClient.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.PublicChannel, "Denied"));
        await AssertErrorAsync(deniedResponse, HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied);

        var publicConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            $"Public {Guid.NewGuid():N}");
        var privateConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            $"Private {Guid.NewGuid():N}");

        var adminList = await GetConversationListAsync(adminClient);
        var normalList = await GetConversationListAsync(normalClient);
        Assert.True(adminList.Complete);
        Assert.True(normalList.Complete);
        Assert.Contains(adminList.Conversations, conversation => conversation.Id == publicConversation.Id);
        Assert.Contains(adminList.Conversations, conversation => conversation.Id == privateConversation.Id);
        Assert.Contains(normalList.Conversations, conversation => conversation.Id == publicConversation.Id);
        Assert.DoesNotContain(normalList.Conversations, conversation => conversation.Id == privateConversation.Id);
        Assert.All(adminList.Conversations, conversation =>
        {
            Assert.Equal(0, conversation.LastMessageId);
            Assert.Equal(0, conversation.UnreadCount);
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var creatorMemberships = await dbContext.ConversationMembers.AsNoTracking()
            .Where(member =>
                member.UserId == adminId &&
                (member.ConversationId == publicConversation.Id || member.ConversationId == privateConversation.Id))
            .ToArrayAsync();
        Assert.Equal(2, creatorMemberships.Length);
        Assert.All(creatorMemberships, member =>
        {
            Assert.Equal(ConversationMemberRole.Administrator, member.Role);
            Assert.Equal(0, member.LastReadMessageId);
        });
    }

    [Fact]
    public async Task DirectCreation_WhenOrderRacesOrRowIsDeleted_ReusesPermanentConversation()
    {
        var firstName = CreateUserName("direct-first");
        var secondName = CreateUserName("direct-second");
        var firstId = await factory.CreateUserAsync(firstName, ExistingPassword);
        var secondId = await factory.CreateUserAsync(secondName, ExistingPassword);
        using var firstClient = await CreateAuthenticatedClientAsync(firstName);
        using var secondClient = await CreateAuthenticatedClientAsync(secondName);

        using var firstResponse = await firstClient.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: secondId));
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstView = (await firstResponse.Content.ReadFromJsonAsync<ConversationDto>())!;
        Assert.Equal(secondName, firstView.Name);

        using var reversedResponse = await secondClient.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: firstId));
        Assert.Equal(HttpStatusCode.OK, reversedResponse.StatusCode);
        var secondView = (await reversedResponse.Content.ReadFromJsonAsync<ConversationDto>())!;
        Assert.Equal(firstView.Id, secondView.Id);
        Assert.Equal(firstName, secondView.Name);

        var members = await GetMembersAsync(firstClient, firstView.Id);
        Assert.Equal(
            new[] { firstId, secondId }.Order(),
            members.Members.Select(member => member.UserId).Order());
        Assert.All(members.Members, member => Assert.Equal(ConversationMemberRole.Member, member.Role));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var conversation = await dbContext.Conversations.SingleAsync(candidate => candidate.Id == firstView.Id);
            conversation.MarkDeleted(conversation.UpdatedAt);
            await dbContext.SaveChangesAsync();
        }

        using (var deletedResponse = await firstClient.GetAsync($"/api/conversations/{firstView.Id:D}"))
        {
            await AssertErrorAsync(
                deletedResponse,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }
        Assert.DoesNotContain(
            (await GetConversationListAsync(firstClient)).Conversations,
            conversation => conversation.Id == firstView.Id);

        using var restoreResponse = await firstClient.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: secondId));
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        Assert.Equal(firstView.Id, (await restoreResponse.Content.ReadFromJsonAsync<ConversationDto>())!.Id);

        await AssertConcurrentDirectSingletonAsync();

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(
            2,
            await verificationContext.ConversationMembers.CountAsync(member => member.ConversationId == firstView.Id));
    }

    [Fact]
    public async Task PrivateMembers_WhenManagedDynamically_EnforceOverrideIdempotencyAndRevocation()
    {
        var creatorName = CreateUserName("private-creator");
        var localAdminName = CreateUserName("private-local-admin");
        var memberName = CreateUserName("private-member");
        var targetName = CreateUserName("private-target");
        var overrideName = CreateUserName("private-override");
        await factory.CreateUserAsync(creatorName, ExistingPassword, isAdmin: true);
        var localAdminId = await factory.CreateUserAsync(localAdminName, ExistingPassword);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        var targetId = await factory.CreateUserAsync(targetName, ExistingPassword);
        await factory.CreateUserAsync(overrideName, ExistingPassword, isAdmin: true);
        using var creatorClient = await CreateAuthenticatedClientAsync(creatorName);
        using var localAdminClient = await CreateAuthenticatedClientAsync(localAdminName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        using var overrideClient = await CreateAuthenticatedClientAsync(overrideName);
        var conversation = await CreateChannelAsync(
            creatorClient,
            ConversationType.PrivateChannel,
            $"Members {Guid.NewGuid():N}");

        await UpsertMemberAsync(
            creatorClient,
            conversation.Id,
            localAdminId,
            ConversationMemberRole.Administrator,
            HttpStatusCode.Created);
        await UpsertMemberAsync(
            creatorClient,
            conversation.Id,
            memberId,
            ConversationMemberRole.Member,
            HttpStatusCode.Created);

        using (var overrideRead = await overrideClient.GetAsync($"/api/conversations/{conversation.Id:D}"))
        {
            await AssertErrorAsync(
                overrideRead,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }
        Assert.Contains(
            (await GetMembersAsync(overrideClient, conversation.Id)).Members,
            member => member.UserId == memberId);

        using (var deniedWrite = await memberClient.PostAsJsonAsync(
                   $"/api/conversations/{conversation.Id:D}/members",
                   new UpsertConversationMemberRequest(targetId, ConversationMemberRole.Member)))
        {
            await AssertErrorAsync(deniedWrite, HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied);
        }

        var inserted = await UpsertMemberAsync(
            localAdminClient,
            conversation.Id,
            targetId,
            ConversationMemberRole.Member,
            HttpStatusCode.Created);
        Assert.Equal(0, inserted.LastReadMessageId);
        var repeated = await UpsertMemberAsync(
            localAdminClient,
            conversation.Id,
            targetId,
            ConversationMemberRole.Member,
            HttpStatusCode.OK);
        Assert.Equal(inserted.JoinedAt, repeated.JoinedAt);
        var promoted = await UpsertMemberAsync(
            localAdminClient,
            conversation.Id,
            targetId,
            ConversationMemberRole.Administrator,
            HttpStatusCode.OK);
        Assert.Equal(ConversationMemberRole.Administrator, promoted.Role);

        using (var firstRemove = await localAdminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{targetId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, firstRemove.StatusCode);
        }
        using (var repeatedRemove = await localAdminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{targetId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, repeatedRemove.StatusCode);
        }

        var firstConcurrentUpsert = localAdminClient.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id:D}/members",
            new UpsertConversationMemberRequest(targetId, ConversationMemberRole.Member));
        var secondConcurrentUpsert = localAdminClient.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id:D}/members",
            new UpsertConversationMemberRequest(targetId, ConversationMemberRole.Member));
        using (var firstConcurrentResponse = await firstConcurrentUpsert)
        using (var secondConcurrentResponse = await secondConcurrentUpsert)
        {
            Assert.Equal(
                [HttpStatusCode.OK, HttpStatusCode.Created],
                new[] { firstConcurrentResponse.StatusCode, secondConcurrentResponse.StatusCode }.Order());
            var firstConcurrentMember = await firstConcurrentResponse.Content.ReadFromJsonAsync<ConversationMemberDto>();
            var secondConcurrentMember = await secondConcurrentResponse.Content.ReadFromJsonAsync<ConversationMemberDto>();
            Assert.Equal(0, firstConcurrentMember!.LastReadMessageId);
            Assert.Equal(0, secondConcurrentMember!.LastReadMessageId);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            Assert.Equal(
                1,
                await dbContext.ConversationMembers.CountAsync(candidate =>
                    candidate.ConversationId == conversation.Id && candidate.UserId == targetId));
        }

        using (var revoke = await localAdminClient.DeleteAsync(
                   $"/api/conversations/{conversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }
        using (var revokedRead = await memberClient.GetAsync($"/api/conversations/{conversation.Id:D}"))
        {
            await AssertErrorAsync(
                revokedRead,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }
        using (var revokedMembers = await memberClient.GetAsync(
                   $"/api/conversations/{conversation.Id:D}/members"))
        {
            await AssertErrorAsync(
                revokedMembers,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }
        Assert.DoesNotContain(
            (await GetConversationListAsync(memberClient)).Conversations,
            candidate => candidate.Id == conversation.Id);
        await using (var accessScope = factory.Services.CreateAsyncScope())
        {
            var dbContext = accessScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            Assert.False(await ConversationAccessQuery
                .VisibleTo(dbContext, memberId)
                .AnyAsync(candidate => candidate.Id == conversation.Id));
        }
    }

    [Fact]
    public async Task MemberOperations_WhenConversationTypeOrInputIsWrong_ReturnStableErrors()
    {
        var adminName = CreateUserName("type-admin");
        var firstName = CreateUserName("type-first");
        var secondName = CreateUserName("type-second");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var firstId = await factory.CreateUserAsync(firstName, ExistingPassword);
        var secondId = await factory.CreateUserAsync(secondName, ExistingPassword);
        var disabledId = await factory.CreateUserAsync(
            CreateUserName("type-disabled"),
            ExistingPassword,
            isDisabled: true);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var firstClient = await CreateAuthenticatedClientAsync(firstName);
        using var secondClient = await CreateAuthenticatedClientAsync(secondName);
        var publicConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            $"Type {Guid.NewGuid():N}");
        var directConversation = await CreateDirectAsync(firstClient, secondId, HttpStatusCode.Created);

        using (var publicMembers = await firstClient.GetAsync(
                   $"/api/conversations/{publicConversation.Id:D}/members"))
        {
            await AssertErrorAsync(
                publicMembers,
                HttpStatusCode.Conflict,
                ApiErrorCodes.ConversationTypeConflict);
        }
        using (var publicWrite = await firstClient.PostAsJsonAsync(
                   $"/api/conversations/{publicConversation.Id:D}/members",
                   new UpsertConversationMemberRequest(firstId, ConversationMemberRole.Member)))
        {
            await AssertErrorAsync(
                publicWrite,
                HttpStatusCode.Conflict,
                ApiErrorCodes.ConversationTypeConflict);
        }
        using (var directWrite = await firstClient.PostAsJsonAsync(
                   $"/api/conversations/{directConversation.Id:D}/members",
                   new UpsertConversationMemberRequest(firstId, ConversationMemberRole.Member)))
        {
            await AssertErrorAsync(
                directWrite,
                HttpStatusCode.Conflict,
                ApiErrorCodes.ConversationTypeConflict);
        }
        using (var outsiderRead = await adminClient.GetAsync($"/api/conversations/{directConversation.Id:D}"))
        {
            await AssertErrorAsync(
                outsiderRead,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }
        using (var outsiderMembers = await adminClient.GetAsync(
                   $"/api/conversations/{directConversation.Id:D}/members"))
        {
            await AssertErrorAsync(
                outsiderMembers,
                HttpStatusCode.Forbidden,
                ApiErrorCodes.ConversationAccessRevoked);
        }
        using (var selfDirect = await firstClient.PostAsJsonAsync(
                   "/api/conversations",
                   new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: firstId)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, selfDirect.StatusCode);
            var error = await selfDirect.Content.ReadFromJsonAsync<ApiErrorResponse>();
            Assert.Equal(ApiErrorCodes.ValidationFailed, error!.Code);
            Assert.Equal(["participantUserId"], error.Details!.Keys);
        }
        using (var missingTarget = await firstClient.PostAsJsonAsync(
                   "/api/conversations",
                   new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: Guid.NewGuid())))
        {
            await AssertErrorAsync(missingTarget, HttpStatusCode.NotFound, ApiErrorCodes.UserNotFound);
        }
        using (var disabledTarget = await firstClient.PostAsJsonAsync(
                   "/api/conversations",
                   new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: disabledId)))
        {
            await AssertErrorAsync(disabledTarget, HttpStatusCode.NotFound, ApiErrorCodes.UserNotFound);
        }

        var logs = string.Join(Environment.NewLine, factory.LogMessages);
        Assert.DoesNotContain(publicConversation.Name, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(firstName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secondName, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChannelCreation_WhenJwtAdministratorWasDemoted_RechecksDatabaseInsideWriteTransaction()
    {
        var adminName = CreateUserName("demoted-admin");
        var adminId = await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var admin = await dbContext.Users.SingleAsync(user => user.Id == adminId);
            dbContext.Entry(admin).Property(user => user.IsAdmin).CurrentValue = false;
            await dbContext.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.PrivateChannel, "Should not exist"));
        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied);
    }

    [Fact]
    public async Task ConversationWrite_WhenSqliteIsBusy_ReturnsStableServiceUnavailable()
    {
        using var busyFactory = new RelayCoveWebApplicationFactory(1_000, 1_000, databaseTimeoutSeconds: 1);
        await busyFactory.InitializeDatabaseAsync();
        var adminName = CreateUserName("busy-admin");
        await busyFactory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(busyFactory, adminName);
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
            "/api/conversations",
            new CreateConversationRequest(ConversationType.PublicChannel, "Busy channel"));

        await AssertErrorAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            ApiErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task ConversationListService_WhenRead_UsesOneAuthoritativeDatabaseCommand()
    {
        var userId = await factory.CreateUserAsync(
            CreateUserName("single-query"),
            ExistingPassword);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ConversationQueryService>();
        var logOffset = factory.LogMessages.Count;

        var result = await service.ListAsync(userId, CancellationToken.None);

        Assert.Equal(ConversationOperationStatus.Success, result.Status);
        Assert.True(result.Value!.Complete);
        var databaseCommands = factory.LogMessages
            .Skip(logOffset)
            .Where(message =>
                message.Contains("Executed DbCommand", StringComparison.Ordinal) &&
                message.Contains("SELECT", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(databaseCommands);
    }

    private async Task AssertConcurrentDirectSingletonAsync()
    {
        var firstName = CreateUserName("race-first");
        var secondName = CreateUserName("race-second");
        var firstId = await factory.CreateUserAsync(firstName, ExistingPassword);
        var secondId = await factory.CreateUserAsync(secondName, ExistingPassword);
        using var firstClient = await CreateAuthenticatedClientAsync(firstName);
        using var secondClient = await CreateAuthenticatedClientAsync(secondName);

        var firstTask = firstClient.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: secondId));
        var secondTask = secondClient.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: firstId));
        using var firstResponse = await firstTask;
        using var secondResponse = await secondTask;

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Created],
            new[] { firstResponse.StatusCode, secondResponse.StatusCode }.Order());
        var firstConversation = (await firstResponse.Content.ReadFromJsonAsync<ConversationDto>())!;
        var secondConversation = (await secondResponse.Content.ReadFromJsonAsync<ConversationDto>())!;
        Assert.Equal(firstConversation.Id, secondConversation.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(
            2,
            await dbContext.ConversationMembers.CountAsync(
                member => member.ConversationId == firstConversation.Id));
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
            new LoginRequest(userName, ExistingPassword, "conversation-test", "1.0.0"));
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
        var conversation = (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
        Assert.Equal($"/api/conversations/{conversation.Id:D}", response.Headers.Location!.OriginalString);
        return conversation;
    }

    private static async Task<ConversationDto> CreateDirectAsync(
        HttpClient client,
        Guid participantUserId,
        HttpStatusCode expectedStatus)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: participantUserId));
        Assert.Equal(expectedStatus, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<ConversationMemberDto> UpsertMemberAsync(
        HttpClient client,
        Guid conversationId,
        Guid userId,
        ConversationMemberRole role,
        HttpStatusCode expectedStatus)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/members",
            new UpsertConversationMemberRequest(userId, role));
        Assert.Equal(expectedStatus, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationMemberDto>())!;
    }

    private static async Task<ConversationListResponse> GetConversationListAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/conversations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationListResponse>())!;
    }

    private static async Task<ConversationMemberListResponse> GetMembersAsync(
        HttpClient client,
        Guid conversationId)
    {
        using var response = await client.GetAsync($"/api/conversations/{conversationId:D}/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationMemberListResponse>())!;
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
