namespace RelayCove.Server.Services;

public sealed record AccessTokenResult(string Token, DateTime ExpiresAt)
{
    public override string ToString()
    {
        return $"{nameof(AccessTokenResult)} {{ Token = [REDACTED], ExpiresAt = {ExpiresAt:O} }}";
    }
}
