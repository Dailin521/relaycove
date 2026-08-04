namespace RelayCove.Client.Updates;

internal sealed class ClientUpdateLoginPreflight
{
    private long latestAttempt;

    public async Task<bool> RunAsync(
        string serverAddress,
        Func<string, Task<bool>>? preflightAsync,
        Func<string, Task> loginAsync)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        ArgumentNullException.ThrowIfNull(loginAsync);

        var normalizedServerAddress = serverAddress.Trim();
        var attempt = Interlocked.Increment(ref latestAttempt);
        if (preflightAsync is not null && !await preflightAsync(normalizedServerAddress))
        {
            return false;
        }

        if (Volatile.Read(ref latestAttempt) != attempt)
        {
            return false;
        }

        await loginAsync(normalizedServerAddress);
        return true;
    }
}
