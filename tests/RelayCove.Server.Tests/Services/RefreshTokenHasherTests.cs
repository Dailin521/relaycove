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

        var first = hasher.HashToken(rawToken);
        var second = hasher.HashToken(rawToken);

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
        Assert.Equal(RefreshTokenHasher.EncodedHashLength, first.Length);
        Assert.True(RefreshTokenHasher.IsValidHash(first));
        Assert.DoesNotContain(rawToken, first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64url")]
    [InlineData("___________________________________________=")]
    public void HashToken_WhenTokenIsInvalid_ThrowsWithoutEchoingToken(string rawToken)
    {
        var exception = Assert.Throws<ArgumentException>(() => hasher.HashToken(rawToken));

        if (rawToken.Length > 0)
        {
            Assert.DoesNotContain(rawToken, exception.Message, StringComparison.Ordinal);
        }
    }
}
