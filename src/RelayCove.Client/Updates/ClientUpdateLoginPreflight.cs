namespace RelayCove.Client.Updates;

internal static class ClientUpdateLoginPreflight
{
    public static async Task<bool> RunAsync(
        string serverAddress,
        Func<string, Task<bool>>? preflightAsync,
        Func<string, Task> loginAsync)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        ArgumentNullException.ThrowIfNull(loginAsync);

        var normalizedServerAddress = serverAddress.Trim();
        if (preflightAsync is not null && !await preflightAsync(normalizedServerAddress))
        {
            return false;
        }

        await loginAsync(normalizedServerAddress);
        return true;
    }
}
