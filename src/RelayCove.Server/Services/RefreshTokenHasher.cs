using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace RelayCove.Server.Services;

public sealed class RefreshTokenHasher
{
    public const int RawTokenByteLength = 32;
    public const int EncodedTokenLength = 43;
    public const int EncodedHashLength = 43;

    public RawRefreshToken GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RawTokenByteLength);
        try
        {
            return new RawRefreshToken(WebEncoders.Base64UrlEncode(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool TryHashToken(string? rawToken, out RefreshTokenHash tokenHash)
    {
        if (!TryParse(rawToken, out var parsedToken))
        {
            tokenHash = default;
            return false;
        }

        tokenHash = HashToken(parsedToken);
        return true;
    }

    public RefreshTokenHash HashToken(RawRefreshToken rawToken)
    {
        var rawTokenText = rawToken.Reveal();

        byte[] rawTokenBytes;
        try
        {
            rawTokenBytes = WebEncoders.Base64UrlDecode(rawTokenText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("A validated refresh token became invalid.", exception);
        }

        try
        {
            return new RefreshTokenHash(WebEncoders.Base64UrlEncode(SHA256.HashData(rawTokenBytes)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawTokenBytes);
        }
    }

    public static bool IsValidHash(string? tokenHash) =>
        tokenHash is { Length: EncodedHashLength } && tokenHash.All(IsBase64UrlCharacter);

    private static bool TryParse(string? rawToken, out RawRefreshToken parsedToken)
    {
        parsedToken = default;
        if (rawToken is not { Length: EncodedTokenLength } || !rawToken.All(IsBase64UrlCharacter))
        {
            return false;
        }

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(rawToken);
            try
            {
                if (bytes.Length != RawTokenByteLength)
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }

        parsedToken = new RawRefreshToken(rawToken);
        return true;
    }

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '-'
        or '_';
}
