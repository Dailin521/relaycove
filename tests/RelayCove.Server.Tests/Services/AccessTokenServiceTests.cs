using System.IdentityModel.Tokens.Jwt;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Options;

namespace RelayCove.Server.Tests.Services;

public sealed class AccessTokenServiceTests
{
    [Fact]
    public void CreateToken_WhenClockHasSubMillisecondPrecision_UsesStrictClaimsAndRedactsToken()
    {
        var baseTime = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero).AddTicks(4321);
        var timeProvider = new StubTimeProvider(baseTime);
        var options = AuthenticationOptionsValidatorTests.CreateValidOptions();
        var service = new AccessTokenService(
            Microsoft.Extensions.Options.Options.Create(options),
            new ServerClock(timeProvider));
        var user = CreateUser();

        var result = service.CreateToken(user);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);

        Assert.Equal(AccessTokenService.AccessTokenType, token.Header.Typ);
        Assert.Equal("HS256", token.Header.Alg);
        Assert.Equal(options.Issuer, token.Issuer);
        Assert.Equal([options.Audience], token.Audiences);
        Assert.Equal(user.Id.ToString("D").ToLowerInvariant(), token.Subject);
        Assert.NotNull(token.Id);
        Assert.Equal(0, result.ExpiresAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(baseTime.UtcDateTime.AddMinutes(options.AccessTokenMinutes).AddTicks(-4321), result.ExpiresAt);
        Assert.DoesNotContain(result.Token, result.ToString(), StringComparison.Ordinal);
        Assert.Contains("Token = [REDACTED]", result.ToString(), StringComparison.Ordinal);
    }

    private static User CreateUser() => new(
        Guid.Parse("2aabed19-12de-48c7-a173-4c8938111bcc"),
        "alice",
        "Alice",
        "password-hash",
        isAdmin: false,
        isDisabled: false,
        new DateTime(2026, 8, 3, 3, 0, 0, DateTimeKind.Utc),
        new UserNameNormalizer());

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
