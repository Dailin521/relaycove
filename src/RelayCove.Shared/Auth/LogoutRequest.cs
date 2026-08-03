namespace RelayCove.Shared.Auth;

public sealed record LogoutRequest(string RefreshToken)
{
    public override string ToString()
    {
        return $"{nameof(LogoutRequest)} {{ RefreshToken = [REDACTED] }}";
    }
}
