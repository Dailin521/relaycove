using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class JwtAuthenticationTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Me_WhenJwtValidationInvariantIsBroken_ReturnsOneSanitizedChallenge()
    {
        var userName = $"jwt-{Guid.NewGuid():N}";
        var userId = await factory.CreateUserAsync(userName, "correct-horse-battery-staple");
        using var client = factory.CreateClient();
        string[] invalidTokens =
        [
            CreateToken(userId.ToString("D"), issuer: "wrong-issuer"),
            CreateToken(userId.ToString("D"), audience: "wrong-audience"),
            CreateToken(userId.ToString("D"), tokenType: "JWT"),
            CreateToken("not-a-guid"),
            CreateToken(userId.ToString("D"), expiresAt: DateTime.UtcNow.AddMinutes(-2)),
            CreateToken(userId.ToString("D"), signed: false),
        ];

        foreach (var token in invalidTokens)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var challenge = Assert.Single(response.Headers.WwwAuthenticate);
            Assert.Equal("Bearer", challenge.Scheme);
            Assert.Null(challenge.Parameter);
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            Assert.Equal(ApiErrorCodes.AuthenticationRequired, error!.Code);
            Assert.DoesNotContain("expired", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("signature", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Me_WhenJwtSignatureIsTampered_ReturnsSanitizedChallenge()
    {
        var userName = $"jwt-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, "correct-horse-battery-staple");
        using var client = factory.CreateClient();
        using var loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new RelayCove.Shared.Auth.LoginRequest(userName, "correct-horse-battery-staple", "device", "1.0.0"));
        var login = (await loginResponse.Content.ReadFromJsonAsync<RelayCove.Shared.Auth.LoginResponse>())!;
        var lastCharacter = login.AccessToken[^1] == 'A' ? 'B' : 'A';
        var tamperedToken = login.AccessToken[..^1] + lastCharacter;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tamperedToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(Assert.Single(response.Headers.WwwAuthenticate).Parameter);
    }

    private string CreateToken(
        string subject,
        string issuer = "RelayCove.Server",
        string audience = "RelayCove.Client",
        string tokenType = AccessTokenService.AccessTokenType,
        DateTime? expiresAt = null,
        bool signed = true)
    {
        var now = DateTime.UtcNow;
        var expiration = expiresAt ?? now.AddMinutes(5);
        var notBefore = expiration <= now ? expiration.AddMinutes(-5) : now.AddMinutes(-1);
        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
        ];
        var payload = new JwtPayload(issuer, audience, claims, notBefore, expiration, notBefore);
        var header = signed
            ? new JwtHeader(new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(factory.SigningKey)),
                SecurityAlgorithms.HmacSha256))
            : new JwtHeader();
        header[JwtHeaderParameterNames.Typ] = tokenType;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }
}
