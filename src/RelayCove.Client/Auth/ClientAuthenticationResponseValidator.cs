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
            IsValidToken(response.AccessToken) &&
            IsValidToken(response.RefreshToken) &&
            response.ExpiresAt > now &&
            IsRequiredText(response.ServerVersion, MaximumVersionLength) &&
            IsRequiredText(response.MinimumSupportedClientVersion, MaximumVersionLength);
    }

    private static bool IsRequiredText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static bool IsValidToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumTokenLength &&
        !value.Any(char.IsWhiteSpace);
}
