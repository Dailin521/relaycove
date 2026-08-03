namespace RelayCove.Server.Services;

public readonly record struct RawRefreshToken
{
    private readonly string value;

    internal RawRefreshToken(string value)
    {
        this.value = value;
    }

    internal string Reveal() => value;

    public override string ToString() => "[REDACTED]";
}
