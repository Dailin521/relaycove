using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Errors;

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

    private static string CreateUserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
