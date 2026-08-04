using System.Net.Http.Headers;
using RelayCove.Shared.Auth;

namespace RelayCove.Client.Auth;

internal static class ClientAuthenticationResponseValidator
{
    private const int MaximumDisplayNameLength = 128;
    private const int MaximumTokenLength = 16 * 1024;
    private const int MaximumVersionLength = 64;

    public static bool IsValid(LoginResponse? response, DateTimeOffset now)
    {
        return response is not null &&
            response.UserId != Guid.Empty &&
            IsRequiredText(response.DisplayName, MaximumDisplayNameLength) &&
            IsValidAccessToken(response.AccessToken) &&
            response.AccessTokenVersion >= 0 &&
            IsValidToken(response.RefreshToken) &&
            response.ExpiresAt > now &&
            IsRequiredText(response.ServerVersion, MaximumVersionLength) &&
            IsRequiredText(response.MinimumSupportedClientVersion, MaximumVersionLength);
    }

    private static bool IsRequiredText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    public static bool IsValidRefreshToken(string? value) =>
        IsValidToken(value);

    private static bool IsValidToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumTokenLength &&
        !value.Any(char.IsWhiteSpace);

    private static bool IsValidAccessToken(string? value)
    {
        return IsValidToken(value) &&
            AuthenticationHeaderValue.TryParse($"Bearer {value}", out var header) &&
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(header.Parameter, value, StringComparison.Ordinal);
    }
}
