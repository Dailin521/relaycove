using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class RefreshTokenHasherTests
{
    private readonly RefreshTokenHasher hasher = new();

    [Fact]
    public void HashToken_WhenTokenIsValid_ReturnsDeterministicBase64UrlSha256()
    {
        var rawBytes = Enumerable.Range(0, RefreshTokenHasher.RawTokenByteLength)
            .Select(value => (byte)value)
            .ToArray();
        var rawToken = WebEncoders.Base64UrlEncode(rawBytes);
        var expected = WebEncoders.Base64UrlEncode(SHA256.HashData(rawBytes));

        var firstParsed = hasher.TryHashToken(rawToken, out var first);
        var secondParsed = hasher.TryHashToken(rawToken, out var second);

        Assert.True(firstParsed);
        Assert.True(secondParsed);
        Assert.Equal(expected, first.Value);
        Assert.Equal(first, second);
        Assert.Equal(RefreshTokenHasher.EncodedHashLength, first.Value.Length);
        Assert.True(RefreshTokenHasher.IsValidHash(first.Value));
        Assert.DoesNotContain(rawToken, first.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", first.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64url")]
    [InlineData("___________________________________________=")]
    public void TryHashToken_WhenTokenIsInvalid_ReturnsFalseWithoutThrowing(string rawToken)
    {
        var parsed = hasher.TryHashToken(rawToken, out var tokenHash);

        Assert.False(parsed);
        Assert.Equal(default, tokenHash);
    }

    [Fact]
    public void GenerateToken_WhenCalled_ReturnsDistinctRedactedValidTokens()
    {
        var first = hasher.GenerateToken();
        var second = hasher.GenerateToken();

        Assert.NotEqual(first, second);
        Assert.Equal("[REDACTED]", first.ToString());
        Assert.True(hasher.TryHashToken(first.Reveal(), out _));
        Assert.True(hasher.TryHashToken(second.Reveal(), out _));
    }
}
