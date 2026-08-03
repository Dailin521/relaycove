using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace RelayCove.Server.Services;

public sealed class RefreshTokenHasher
{
    public const int RawTokenByteLength = 32;
    public const int EncodedTokenLength = 43;
    public const int EncodedHashLength = 43;

    public string HashToken(string rawToken)
    {
        ArgumentNullException.ThrowIfNull(rawToken);

        byte[] rawTokenBytes;
        try
        {
            if (rawToken.Length != EncodedTokenLength)
            {
                throw new FormatException();
            }

            rawTokenBytes = WebEncoders.Base64UrlDecode(rawToken);
            if (rawTokenBytes.Length != RawTokenByteLength)
            {
                CryptographicOperations.ZeroMemory(rawTokenBytes);
                throw new FormatException();
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Refresh tokens must be 32-byte Base64Url values.", nameof(rawToken), exception);
        }

        try
        {
            return WebEncoders.Base64UrlEncode(SHA256.HashData(rawTokenBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawTokenBytes);
        }
    }

    public static bool IsValidHash(string? tokenHash) =>
        tokenHash is { Length: EncodedHashLength } && tokenHash.All(IsBase64UrlCharacter);

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '-'
        or '_';
}
