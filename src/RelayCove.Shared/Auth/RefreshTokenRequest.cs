namespace RelayCove.Shared.Auth;

public sealed record RefreshTokenRequest(string RefreshToken)
{
    public override string ToString()
    {
        return $"{nameof(RefreshTokenRequest)} {{ RefreshToken = [REDACTED] }}";
    }
}
