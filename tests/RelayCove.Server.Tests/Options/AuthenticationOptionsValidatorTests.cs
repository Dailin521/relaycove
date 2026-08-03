using System.Security.Cryptography;
using RelayCove.Server.Options;

namespace RelayCove.Server.Tests.Options;

public sealed class AuthenticationOptionsValidatorTests
{
    private readonly AuthenticationOptionsValidator validator = new();

    [Fact]
    public void Validate_WhenOptionsAreSecure_ReturnsSuccess()
    {
        var options = CreateValidOptions();

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Validate_WhenSigningKeyIsMissingOrShort_ReturnsFailure(int keyLength)
    {
        var options = CreateValidOptions();
        options.SigningKey = keyLength == 0
            ? string.Empty
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(keyLength));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("SigningKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenSigningKeyIsNotBase64_ReturnsFailureWithoutEchoingValue()
    {
        const string signingKey = "not-a-base64-signing-key";
        var options = CreateValidOptions();
        options.SigningKey = signingKey;

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(signingKey, string.Join(' ', result.Failures), StringComparison.Ordinal);
    }

    internal static AuthenticationOptions CreateValidOptions() => new()
    {
        Issuer = "RelayCove.Server",
        Audience = "RelayCove.Client",
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
        ClockSkewSeconds = 30,
        ServerVersion = "1.0.0",
        MinimumSupportedClientVersion = "1.0.0",
        LoginPermitLimit = 10,
        RefreshPermitLimit = 60,
        RateLimitWindowSeconds = 60,
    };
}
