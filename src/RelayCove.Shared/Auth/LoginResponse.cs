namespace RelayCove.Shared.Auth;

public sealed record LoginResponse(
    Guid UserId,
    string DisplayName,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string ServerVersion,
    string MinimumSupportedClientVersion)
{
    public override string ToString()
    {
        return $"{nameof(LoginResponse)} {{ UserId = {UserId:D}, DisplayName = {DisplayName}, AccessToken = [REDACTED], RefreshToken = [REDACTED], ExpiresAt = {ExpiresAt:O}, ServerVersion = {ServerVersion}, MinimumSupportedClientVersion = {MinimumSupportedClientVersion} }}";
    }
}
