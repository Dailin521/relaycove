using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class AuthenticationEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string Password = "correct-horse-battery-staple";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LoginAndMe_WhenCredentialsAreValid_ReturnTokensAndCurrentUserWithoutPersistingRawToken()
    {
        var userName = CreateUserName();
        var userId = await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var client = factory.CreateClient();

        var loginResponse = await LoginAsync(client, userName, Password);

        Assert.Equal(userId, loginResponse.UserId);
        Assert.Equal(userName, loginResponse.DisplayName);
        Assert.NotEmpty(loginResponse.AccessToken);
        Assert.Equal(RefreshTokenHasher.EncodedTokenLength, loginResponse.RefreshToken.Length);
        Assert.Equal(TimeSpan.Zero, loginResponse.ExpiresAt.Offset);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginResponse.AccessToken);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var currentUser = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
            Assert.Equal(new CurrentUserResponse(userId, userName, userName, true), currentUser);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var storedToken = await dbContext.RefreshTokens.AsNoTracking().SingleAsync(token => token.UserId == userId);
        var storedUser = await dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
        Assert.Equal(RefreshTokenHasher.EncodedHashLength, storedToken.TokenHash.Length);
        Assert.NotEqual(loginResponse.RefreshToken, storedToken.TokenHash);
        Assert.NotNull(storedUser.LastLoginAt);
        Assert.NotNull(storedUser.LastOnlineAt);
        Assert.Equal(DateTimeKind.Utc, storedUser.LastLoginAt.Value.Kind);
        Assert.Equal(0, storedUser.LastLoginAt.Value.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(storedUser.LastLoginAt, storedUser.UpdatedAt);
    }

    [Fact]
    public async Task Refresh_WhenRotatedSequentially_RevokesEveryPreviousToken()
    {
        var userName = CreateUserName();
        var userId = await factory.CreateUserAsync(userName, Password);
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, userName, Password);

        var firstRotation = await RefreshAsync(client, login.RefreshToken, HttpStatusCode.OK);
        await RefreshAsync(client, login.RefreshToken, HttpStatusCode.Unauthorized);
        var secondRotation = await RefreshAsync(client, firstRotation!.RefreshToken, HttpStatusCode.OK);

        Assert.NotNull(secondRotation);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var tokens = await dbContext.RefreshTokens.AsNoTracking()
            .Where(token => token.UserId == userId)
            .OrderBy(token => token.CreatedAt)
            .ToArrayAsync();
        Assert.Equal(3, tokens.Length);
        Assert.Equal(2, tokens.Count(token => token.RevokedAt is not null));
        Assert.Single(tokens, token => token.RevokedAt is null);
        Assert.DoesNotContain(tokens, token =>
            token.TokenHash == login.RefreshToken ||
            token.TokenHash == firstRotation.RefreshToken ||
            token.TokenHash == secondRotation!.RefreshToken);
    }

    [Fact]
    public async Task Logout_WhenCalledWithValidOrInvalidTokens_IsIdempotentAndRevokesValidToken()
    {
        var userName = CreateUserName();
        await factory.CreateUserAsync(userName, Password);
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, userName, Password);

        await AssertLogoutAsync(client, login.RefreshToken);
        await AssertLogoutAsync(client, login.RefreshToken);
        await AssertLogoutAsync(client, "malformed");
        await AssertLogoutAsync(client, new string('A', RefreshTokenHasher.EncodedTokenLength));
        await RefreshAsync(client, login.RefreshToken, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WhenBodyIsMissing_ReturnsNoContent()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WhenBodyIsMissing_ReturnsGenericAuthenticationFailure()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/auth/refresh", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ApiErrorCodes.AuthenticationFailed, error!.Code);
    }

    [Fact]
    public async Task Me_WhenBearerIsMissing_ReturnsStableAuthenticationRequiredEnvelope()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Null(response.Headers.WwwAuthenticate.Single().Parameter);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ApiErrorCodes.AuthenticationRequired, error!.Code);
        Assert.False(string.IsNullOrEmpty(error.TraceId));
    }

    [Fact]
    public async Task Login_WhenCredentialsAreInvalid_ReturnsOneIndistinguishableAuthenticationFailure()
    {
        var activeUserName = CreateUserName();
        var disabledUserName = CreateUserName();
        await factory.CreateUserAsync(activeUserName, Password);
        await factory.CreateUserAsync(disabledUserName, Password, isDisabled: true);
        using var client = factory.CreateClient();
        LoginRequest[] requests =
        [
            new(CreateUserName(), Password, "test-device", "1.0.0"),
            new(activeUserName, "wrong-password", "test-device", "1.0.0"),
            new(disabledUserName, Password, "test-device", "1.0.0"),
            new("管理员", Password, "test-device", "1.0.0"),
        ];
        var observed = new List<(HttpStatusCode Status, string Code, string Message, bool HasDetails)>();

        foreach (var request in requests)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", request);
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            observed.Add((response.StatusCode, error!.Code, error.Message, error.Details is not null));
        }

        Assert.All(observed, result => Assert.Equal(
            (HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationFailed, "Authentication failed.", false),
            result));
    }

    [Fact]
    public async Task Login_WhenRequestShapeIsInvalid_ReturnsCamelCaseValidationDetails()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("", "", "", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ApiErrorCodes.ValidationFailed, error!.Code);
        Assert.Equal(["clientVersion", "deviceName", "password", "userName"], error.Details!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RequestBinding_WhenJsonIsMalformed_ReturnsStableValidationEnvelope()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ApiErrorCodes.ValidationFailed, error!.Code);
    }

    [Fact]
    public async Task Refresh_WhenTokenIsMalformedUnknownRevokedOrExpired_ReturnsOneFailureShape()
    {
        var userName = CreateUserName();
        var userId = await factory.CreateUserAsync(userName, Password);
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, userName, Password);
        await AssertLogoutAsync(client, login.RefreshToken);
        var expiredRawToken = await CreateExpiredRefreshTokenAsync(userId);
        string?[] tokens =
        [
            "malformed",
            new string('A', RefreshTokenHasher.EncodedTokenLength),
            login.RefreshToken,
            expiredRawToken,
        ];

        foreach (var token in tokens)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(token!));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            Assert.Equal(ApiErrorCodes.AuthenticationFailed, error!.Code);
            Assert.Equal("Authentication failed.", error.Message);
        }
    }

    [Fact]
    public async Task Refresh_WhenTwoRequestsRace_OnlyOneRotationCommits()
    {
        var userName = CreateUserName();
        var userId = await factory.CreateUserAsync(userName, Password);
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, userName, Password);

        var firstTask = client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken));
        var secondTask = client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken));
        using var first = await firstTask;
        using var second = await secondTask;

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Unauthorized],
            new[] { first.StatusCode, second.StatusCode }.Order());
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var tokens = await dbContext.RefreshTokens.AsNoTracking().Where(token => token.UserId == userId).ToArrayAsync();
        Assert.Equal(2, tokens.Length);
        Assert.Single(tokens, token => token.RevokedAt is null);
        Assert.Single(tokens, token => token.RevokedAt is not null);
    }

    [Fact]
    public async Task Me_WhenUserIsDisabledAfterLogin_RejectsStillUnexpiredAccessToken()
    {
        var userName = CreateUserName();
        var userId = await factory.CreateUserAsync(userName, Password);
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, userName, Password);
        await factory.SetUserDisabledAsync(userId, true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ApiErrorCodes.AuthenticationRequired, error!.Code);
    }

    [Fact]
    public async Task Login_WhenPasswordNeedsRehash_UpdatesHashAndActivityInSuccessfulTransaction()
    {
        var userName = CreateUserName();
        var userId = await factory.CreateUserAsync(userName, Password, passwordIterationCount: 10_000);
        string oldHash;
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var beforeContext = beforeScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            oldHash = (await beforeContext.Users.AsNoTracking().SingleAsync(user => user.Id == userId)).PasswordHash;
        }

        using var client = factory.CreateClient();
        await LoginAsync(client, userName, Password);

        await using var afterScope = factory.Services.CreateAsyncScope();
        var afterContext = afterScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var passwordService = afterScope.ServiceProvider.GetRequiredService<PasswordService>();
        var user = await afterContext.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId);
        Assert.NotEqual(oldHash, user.PasswordHash);
        Assert.Equal(PasswordVerificationOutcome.Success, passwordService.VerifyPassword(user, user.PasswordHash, Password));
        Assert.Equal(user.LastLoginAt, user.UpdatedAt);
    }

    [Fact]
    public async Task AuthenticationFlow_WhenObservedThroughLogging_DoesNotExposeSecretsOrTokens()
    {
        var userName = CreateUserName();
        var password = $"password-{Guid.NewGuid():N}";
        var userId = await factory.CreateUserAsync(userName, password);
        using var client = factory.CreateClient();

        var login = await LoginAsync(client, userName, password);
        var refresh = await RefreshAsync(client, login.RefreshToken, HttpStatusCode.OK);
        await AssertLogoutAsync(client, refresh!.RefreshToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var storedHashes = await dbContext.RefreshTokens.AsNoTracking()
            .Where(token => token.UserId == userId)
            .Select(token => token.TokenHash)
            .ToArrayAsync();
        var logs = string.Join(Environment.NewLine, factory.LogMessages);
        Assert.DoesNotContain(password, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(login.AccessToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(login.RefreshToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(refresh.AccessToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(refresh.RefreshToken, logs, StringComparison.Ordinal);
        Assert.All(storedHashes, hash => Assert.DoesNotContain(hash, logs, StringComparison.Ordinal));
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string userName, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, password, "test-device", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task<LoginResponse?> RefreshAsync(
        HttpClient client,
        string refreshToken,
        HttpStatusCode expectedStatus)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(refreshToken));
        Assert.Equal(expectedStatus, response.StatusCode);
        if (response.StatusCode is HttpStatusCode.OK)
        {
            return await response.Content.ReadFromJsonAsync<LoginResponse>();
        }

        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ApiErrorCodes.AuthenticationFailed, error!.Code);
        return null;
    }

    private static async Task AssertLogoutAsync(HttpClient client, string token)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    private async Task<string> CreateExpiredRefreshTokenAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<RefreshTokenHasher>();
        var clock = scope.ServiceProvider.GetRequiredService<ServerClock>();
        var rawToken = hasher.GenerateToken();
        var now = clock.UtcNow;
        dbContext.RefreshTokens.Add(new RelayCove.Server.Data.Entities.RefreshToken(
            Guid.NewGuid(),
            userId,
            hasher.HashToken(rawToken),
            "expired-device",
            now.AddDays(-2),
            now.AddDays(-1)));
        await dbContext.SaveChangesAsync();
        return rawToken.Reveal();
    }

    private static string CreateUserName() => $"user-{Guid.NewGuid():N}";
}
