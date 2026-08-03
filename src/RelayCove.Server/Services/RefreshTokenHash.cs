namespace RelayCove.Server.Services;

public readonly record struct RefreshTokenHash
{
    private readonly string value;

    internal RefreshTokenHash(string value)
    {
        this.value = value;
    }

    internal string Value => value;

    public override string ToString() => "[REDACTED]";
}
