namespace RelayCove.Server.Options;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;

    public int ClockSkewSeconds { get; set; } = 30;

    public string ServerVersion { get; set; } = string.Empty;

    public string MinimumSupportedClientVersion { get; set; } = string.Empty;

    public int LoginPermitLimit { get; set; } = 10;

    public int RefreshPermitLimit { get; set; } = 60;

    public int RateLimitWindowSeconds { get; set; } = 60;
}
