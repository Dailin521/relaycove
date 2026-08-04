using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class AuthenticationRateLimitTests
{
    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public async Task AuthenticationPolicies_WhenLimitsAreExceeded_ReturnStable429WithoutLimitingMe()
    {
        using var factory = new RelayCoveWebApplicationFactory(loginPermitLimit: 2, refreshPermitLimit: 2);
        await factory.InitializeDatabaseAsync();
        var userName = $"rate-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password);
        using var client = factory.CreateClient();
        using var successfulLogin = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, Password, "device", "1.0.0"));
        var login = (await successfulLogin.Content.ReadFromJsonAsync<LoginResponse>())!;
        using var secondLogin = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, "wrong", "device", "1.0.0"));

        using var limitedLogin = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, "wrong", "device", "1.0.0"));

        await AssertRateLimitedAsync(limitedLogin);
        using var firstRefresh = await client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        using var secondRefresh = await client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        using var limitedRefresh = await client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        await AssertRateLimitedAsync(limitedRefresh);
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        using var meResponse = await client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task LoginRateLimit_WhenRequestsArriveThroughLoopbackProxy_PartitionsByForwardedClientAddress()
    {
        using var factory = new RelayCoveWebApplicationFactory(loginPermitLimit: 1, refreshPermitLimit: 10);
        await factory.InitializeDatabaseAsync();
        using var firstClient = CreateProxiedClient(factory, "198.51.100.10");
        using var secondClient = CreateProxiedClient(factory, "198.51.100.11");
        var request = new LoginRequest("missing-user", Password, "device", "1.0.0");

        using var firstResponse = await firstClient.PostAsJsonAsync("/api/auth/login", request);
        using var secondResponse = await secondClient.PostAsJsonAsync("/api/auth/login", request);
        using var limitedFirstResponse = await firstClient.PostAsJsonAsync("/api/auth/login", request);

        Assert.Equal(HttpStatusCode.Unauthorized, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);
        await AssertRateLimitedAsync(limitedFirstResponse);
    }

    private static HttpClient CreateProxiedClient(
        RelayCoveWebApplicationFactory factory,
        string forwardedAddress)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedAddress);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ApiErrorCodes.RateLimitExceeded, error!.Code);
    }
}
