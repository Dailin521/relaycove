using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayCove.Server.Data;
using RelayCove.Server.Realtime;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Realtime;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class AdminUserEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string ExistingPassword = "an existing secure login phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateUser_WhenActorIsAdministrator_CreatesHashOnlyLoginCapableUser()
    {
        var adminUserName = CreateUserName("admin");
        var adminId = await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        var createdUserName = CreateUserName("created");
        var createdPassword = $"created secure phrase {Guid.NewGuid():N}";
        using var client = factory.CreateClient();
        var adminLogin = await LoginAsync(client, adminUserName, ExistingPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);

        using var response = await client.PostAsJsonAsync(
            "/api/admin/users",
            new CreateUserRequest(createdUserName, "Created User", createdPassword, true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AdminUserResponse>();
        Assert.NotNull(created);
        Assert.Equal(createdUserName, created.UserName);
        Assert.Equal("Created User", created.DisplayName);
        Assert.True(created.IsAdmin);
        Assert.False(created.IsDisabled);
        Assert.Equal($"/api/admin/users/{created.UserId:D}", response.Headers.Location!.OriginalString);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var stored = await dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == created.UserId);
        Assert.NotEqual(createdPassword, stored.PasswordHash);
        Assert.NotEmpty(stored.PasswordHash);

        using var loginClient = factory.CreateClient();
        var createdLogin = await LoginAsync(loginClient, createdUserName, createdPassword);
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", createdLogin.AccessToken);
        using var meResponse = await loginClient.SendAsync(meRequest);
        var currentUser = await meResponse.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.True(currentUser!.IsAdmin);

        var logs = string.Join(Environment.NewLine, factory.LogMessages);
        Assert.Contains(adminId.ToString("D"), logs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.UserId.ToString("D"), logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(createdPassword, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(createdUserName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Created User", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.PasswordHash, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUser_WhenActorIsMissingNormalOrDisabledAdmin_ReturnsStableAuthorizationErrors()
    {
        var normalUserName = CreateUserName("normal");
        var adminUserName = CreateUserName("disabled-admin");
        await factory.CreateUserAsync(normalUserName, ExistingPassword);
        var adminId = await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        using var client = factory.CreateClient();
        var request = new CreateUserRequest(
            CreateUserName("denied"),
            "Denied User",
            "a valid denied user phrase",
            false);

        using (var missingResponse = await client.PostAsJsonAsync("/api/admin/users", request))
        {
            await AssertErrorAsync(missingResponse, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
        }

        var normalLogin = await LoginAsync(client, normalUserName, ExistingPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", normalLogin.AccessToken);
        using (var normalResponse = await client.PostAsJsonAsync("/api/admin/users", request))
        {
            await AssertErrorAsync(normalResponse, HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied);
        }

        var adminLogin = await LoginAsync(client, adminUserName, ExistingPassword);
        await factory.SetUserDisabledAsync(adminId, true);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);
        using var disabledResponse = await client.PostAsJsonAsync("/api/admin/users", request);
        await AssertErrorAsync(disabledResponse, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task CreateUser_WhenRequestIsInvalid_ReturnsCamelCaseValidationWithoutEchoingPassword()
    {
        var adminUserName = CreateUserName("validation-admin");
        await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        using var client = factory.CreateClient();
        var adminLogin = await LoginAsync(client, adminUserName, ExistingPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);
        const string invalidPassword = "short";

        using var response = await client.PostAsJsonAsync(
            "/api/admin/users",
            new CreateUserRequest("管理员", "", invalidPassword, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var error = System.Text.Json.JsonSerializer.Deserialize<ApiErrorResponse>(
            body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(ApiErrorCodes.ValidationFailed, error!.Code);
        Assert.Equal(["displayName", "password", "userName"], error.Details!.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(invalidPassword, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUser_WhenNormalizedUserNameExists_ReturnsStableConflict()
    {
        var adminUserName = CreateUserName("duplicate-admin");
        await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        await factory.CreateUserAsync("Duplicate.Name", ExistingPassword);
        using var client = factory.CreateClient();
        var adminLogin = await LoginAsync(client, adminUserName, ExistingPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);

        using var response = await client.PostAsJsonAsync(
            "/api/admin/users",
            new CreateUserRequest("duplicate.name", "Duplicate", "a different secure phrase", false));

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.UserNameAlreadyExists);
    }

    [Fact]
    public async Task CreateUser_WhenSameNormalizedNameRaces_OnlyOneRequestCreatesUser()
    {
        var adminUserName = CreateUserName("race-admin");
        await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        using var client = factory.CreateClient();
        var adminLogin = await LoginAsync(client, adminUserName, ExistingPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);
        var racedUserName = CreateUserName("race-target");
        var firstTask = client.PostAsJsonAsync(
            "/api/admin/users",
            new CreateUserRequest(racedUserName.ToUpperInvariant(), "First", "a first secure race phrase", false));
        var secondTask = client.PostAsJsonAsync(
            "/api/admin/users",
            new CreateUserRequest(racedUserName.ToLowerInvariant(), "Second", "a second secure race phrase", false));

        using var first = await firstTask;
        using var second = await secondTask;

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            new[] { first.StatusCode, second.StatusCode }.Order());
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var normalizer = scope.ServiceProvider.GetRequiredService<UserNameNormalizer>();
        var normalized = normalizer.Normalize(racedUserName);
        Assert.Single(await dbContext.Users.AsNoTracking()
            .Where(user => user.NormalizedUserName == normalized)
            .ToArrayAsync());
    }

    [Fact]
    public async Task CreateUserService_WhenActorIsNotAdministrator_RechecksInsideTransactionAndCreatesNothing()
    {
        var actorId = await factory.CreateUserAsync(CreateUserName("service-normal"), ExistingPassword);
        var targetUserName = CreateUserName("service-target");
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AdminUserService>();

        var result = await service.CreateUserAsync(
            actorId,
            new CreateUserRequest(targetUserName, "Service Target", "a service target secure phrase", false),
            CancellationToken.None);

        Assert.Equal(AdminUserCreationStatus.ActorNotAdministrator, result.Status);
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.False(await dbContext.Users.AnyAsync(user => user.UserName == targetUserName));
    }

    [Fact]
    public async Task UserMaintenance_WhenDisabledRestoredAndPasswordReset_RevokesAllExistingSessions()
    {
        var adminUserName = CreateUserName("maintenance-admin");
        var targetUserName = CreateUserName("maintenance-target");
        const string targetPassword = "a target secure login phrase";
        const string resetPassword = "a reset secure login phrase";
        await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        var targetUserId = await factory.CreateUserAsync(targetUserName, targetPassword);
        using var adminClient = factory.CreateClient();
        using var targetClient = factory.CreateClient();
        var adminLogin = await LoginAsync(adminClient, adminUserName, ExistingPassword);
        var targetLogin = await LoginAsync(targetClient, targetUserName, targetPassword);
        var secondTargetLogin = await LoginAsync(targetClient, targetUserName, targetPassword);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);

        using (var list = await adminClient.GetAsync("/api/admin/users"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var users = await list.Content.ReadFromJsonAsync<AdminUserResponse[]>();
            Assert.Contains(users!, user => user.UserId == targetUserId && user.RetiredAt is null);
        }

        using (var disabled = await adminClient.PutAsJsonAsync(
                   $"/api/admin/users/{targetUserId:D}",
                   new UpdateAdminUserRequest(true)))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            var user = await disabled.Content.ReadFromJsonAsync<AdminUserResponse>();
            Assert.True(user!.IsDisabled);
            Assert.Null(user.RetiredAt);
        }

        await AssertAuthenticationRequiredAsync(targetClient, targetLogin.AccessToken);
        await AssertRefreshRejectedAsync(targetClient, targetLogin.RefreshToken);
        await AssertRefreshRejectedAsync(targetClient, secondTargetLogin.RefreshToken);

        using (var restored = await adminClient.PutAsJsonAsync(
                   $"/api/admin/users/{targetUserId:D}",
                   new UpdateAdminUserRequest(false)))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.False((await restored.Content.ReadFromJsonAsync<AdminUserResponse>())!.IsDisabled);
        }

        var restoredLogin = await LoginAsync(targetClient, targetUserName, targetPassword);
        using (var reset = await adminClient.PostAsJsonAsync(
                   $"/api/admin/users/{targetUserId:D}/reset-password",
                   new ResetUserPasswordRequest(resetPassword)))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        await AssertAuthenticationRequiredAsync(targetClient, restoredLogin.AccessToken);
        await AssertRefreshRejectedAsync(targetClient, restoredLogin.RefreshToken);
        using (var oldPassword = await targetClient.PostAsJsonAsync(
                   "/api/auth/login",
                   new LoginRequest(targetUserName, targetPassword, "admin-test", "1.0.0")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var storedTarget = await dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == targetUserId);
        Assert.Equal(3, storedTarget.AccessTokenVersion);
        Assert.All(
            await dbContext.RefreshTokens.AsNoTracking().Where(token => token.UserId == targetUserId).ToArrayAsync(),
            token => Assert.NotNull(token.RevokedAt));
        _ = await LoginAsync(targetClient, targetUserName, resetPassword);
    }

    [Fact]
    public async Task RetireUser_WhenRequested_PreservesHistoryAndRejectsRestoreOrFutureLogin()
    {
        var adminUserName = CreateUserName("retire-admin");
        var targetUserName = CreateUserName("retire-target");
        const string targetPassword = "a retirement secure phrase";
        await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        var targetUserId = await factory.CreateUserAsync(targetUserName, targetPassword);
        using var adminClient = factory.CreateClient();
        using var targetClient = factory.CreateClient();
        var adminLogin = await LoginAsync(adminClient, adminUserName, ExistingPassword);
        var targetLogin = await LoginAsync(targetClient, targetUserName, targetPassword);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);

        using (var response = await adminClient.DeleteAsync($"/api/admin/users/{targetUserId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await AssertAuthenticationRequiredAsync(targetClient, targetLogin.AccessToken);
        using (var restore = await adminClient.PutAsJsonAsync(
                   $"/api/admin/users/{targetUserId:D}",
                   new UpdateAdminUserRequest(false)))
        {
            await AssertErrorAsync(restore, HttpStatusCode.Conflict, ApiErrorCodes.UserRetired);
        }
        using (var futureLogin = await targetClient.PostAsJsonAsync(
                   "/api/auth/login",
                   new LoginRequest(targetUserName, targetPassword, "admin-test", "1.0.0")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, futureLogin.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var retired = await dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == targetUserId);
        Assert.True(retired.IsDisabled);
        Assert.NotNull(retired.RetiredAt);
        Assert.Equal(targetUserName, retired.UserName);
        Assert.Equal(1, retired.AccessTokenVersion);
    }

    [Fact]
    public async Task UserMaintenance_WhenActorAttemptsSelfDisableOrSelfRetire_ReturnsAccessDenied()
    {
        var adminUserName = CreateUserName("self-admin");
        var adminUserId = await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, adminUserName, ExistingPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        using (var disable = await client.PutAsJsonAsync(
                   $"/api/admin/users/{adminUserId:D}",
                   new UpdateAdminUserRequest(true)))
        {
            await AssertErrorAsync(disable, HttpStatusCode.Conflict, ApiErrorCodes.SelfActionForbidden);
        }

        using var retire = await client.DeleteAsync($"/api/admin/users/{adminUserId:D}");
        await AssertErrorAsync(retire, HttpStatusCode.Conflict, ApiErrorCodes.SelfActionForbidden);
    }

    [Fact]
    public async Task UserMaintenance_WhenTwoActiveAdministratorsDisableEachOtherConcurrently_PreservesOneActiveAdministrator()
    {
        var firstUserName = CreateUserName("concurrent-first-admin");
        var secondUserName = CreateUserName("concurrent-second-admin");
        var firstUserId = await factory.CreateUserAsync(firstUserName, ExistingPassword, isAdmin: true);
        var secondUserId = await factory.CreateUserAsync(secondUserName, ExistingPassword, isAdmin: true);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var firstLogin = await LoginAsync(firstClient, firstUserName, ExistingPassword);
        var secondLogin = await LoginAsync(secondClient, secondUserName, ExistingPassword);
        firstClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstLogin.AccessToken);
        secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondLogin.AccessToken);
        using var release = new ManualResetEventSlim(false);
        var firstRequest = Task.Run(async () =>
        {
            release.Wait();
            return await firstClient.PutAsJsonAsync(
                $"/api/admin/users/{secondUserId:D}",
                new UpdateAdminUserRequest(true));
        });
        var secondRequest = Task.Run(async () =>
        {
            release.Wait();
            return await secondClient.PutAsJsonAsync(
                $"/api/admin/users/{firstUserId:D}",
                new UpdateAdminUserRequest(true));
        });

        release.Set();
        using var firstResponse = await firstRequest;
        using var secondResponse = await secondRequest;

        Assert.DoesNotContain(
            new[] { firstResponse.StatusCode, secondResponse.StatusCode },
            statusCode => statusCode == HttpStatusCode.InternalServerError);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.True(await dbContext.Users.CountAsync(user =>
            user.IsAdmin && !user.IsDisabled && user.RetiredAt == null) >= 1);
    }

    [Fact]
    public async Task DisableUser_WhenAccountRevocationTransportFails_PreservesCommittedState()
    {
        var adminUserName = CreateUserName("revoke-failure-admin");
        var targetUserName = CreateUserName("revoke-failure-target");
        await factory.CreateUserAsync(adminUserName, ExistingPassword, isAdmin: true);
        var targetUserId = await factory.CreateUserAsync(targetUserName, ExistingPassword);
        var transport = new ThrowingAccountAccessRevokedTransport();
        using var failingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAccountAccessRevokedTransport>();
                services.AddSingleton<IAccountAccessRevokedTransport>(transport);
            }));
        using var client = failingFactory.CreateClient();
        var adminLogin = await LoginAsync(client, adminUserName, ExistingPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);

        using var response = await client.PutAsJsonAsync(
            $"/api/admin/users/{targetUserId:D}",
            new UpdateAdminUserRequest(true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.AttemptCount);
        Assert.Equal(targetUserId.ToString("D"), transport.RecipientUserId);
        Assert.Equal(1, transport.MinimumAccessTokenVersion);
        await using var scope = failingFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.True((await dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == targetUserId)).IsDisabled);
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string userName, string password)
    {
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, password, "admin-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
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

    private static async Task AssertAuthenticationRequiredAsync(HttpClient client, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired);
    }

    private static async Task AssertRefreshRejectedAsync(HttpClient client, string refreshToken)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(refreshToken));
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationFailed);
    }

    private sealed class ThrowingAccountAccessRevokedTransport : IAccountAccessRevokedTransport
    {
        private int attemptCount;

        public int AttemptCount => Volatile.Read(ref attemptCount);

        public string? RecipientUserId { get; private set; }

        public long? MinimumAccessTokenVersion { get; private set; }

        public Task SendAsync(
            string recipientUserId,
            AccountAccessRevokedEvent accountAccessRevoked,
            CancellationToken cancellationToken)
        {
            RecipientUserId = recipientUserId;
            MinimumAccessTokenVersion = accountAccessRevoked.MinimumAccessTokenVersion;
            Interlocked.Increment(ref attemptCount);
            throw new InvalidOperationException("Synthetic account access-revoked transport failure.");
        }
    }

    private static string CreateUserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
