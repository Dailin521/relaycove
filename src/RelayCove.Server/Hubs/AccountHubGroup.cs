namespace RelayCove.Server.Hubs;

internal static class AccountHubGroup
{
    public static string For(Guid userId, long accessTokenVersion)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(accessTokenVersion);
        return $"account:{userId:D}:v{accessTokenVersion}";
    }
}
