using Microsoft.Extensions.Options;

namespace RelayCove.Server.Options;

public sealed class AuthenticationOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        var failures = new List<string>();

        RequireText(options.Issuer, "Authentication:Issuer", failures);
        RequireText(options.Audience, "Authentication:Audience", failures);
        RequireText(options.ServerVersion, "Authentication:ServerVersion", failures);
        RequireText(options.MinimumSupportedClientVersion, "Authentication:MinimumSupportedClientVersion", failures);
        ValidateSigningKey(options.SigningKey, failures);
        RequireRange(options.AccessTokenMinutes, 1, 60, "Authentication:AccessTokenMinutes", failures);
        RequireRange(options.RefreshTokenDays, 1, 365, "Authentication:RefreshTokenDays", failures);
        RequireRange(options.ClockSkewSeconds, 0, 300, "Authentication:ClockSkewSeconds", failures);
        RequireRange(options.LoginPermitLimit, 1, 10_000, "Authentication:LoginPermitLimit", failures);
        RequireRange(options.RefreshPermitLimit, 1, 10_000, "Authentication:RefreshPermitLimit", failures);
        RequireRange(options.RateLimitWindowSeconds, 1, 3_600, "Authentication:RateLimitWindowSeconds", failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static byte[] DecodeSigningKey(string signingKey)
    {
        try
        {
            return Convert.FromBase64String(signingKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Authentication:SigningKey must be valid Base64.", exception);
        }
    }

    private static void ValidateSigningKey(string signingKey, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            failures.Add("Authentication:SigningKey is required and must not be committed to appsettings.");
            return;
        }

        try
        {
            var bytes = Convert.FromBase64String(signingKey);
            if (bytes.Length < 32)
            {
                failures.Add("Authentication:SigningKey must decode to at least 32 bytes.");
            }

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
        catch (FormatException)
        {
            failures.Add("Authentication:SigningKey must be valid Base64.");
        }
    }

    private static void RequireText(string value, string path, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{path} is required.");
        }
    }

    private static void RequireRange(int value, int minimum, int maximum, string path, ICollection<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{path} must be between {minimum} and {maximum}.");
        }
    }
}
