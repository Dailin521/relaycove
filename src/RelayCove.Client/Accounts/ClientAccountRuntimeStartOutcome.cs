using RelayCove.Client.Sync;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed record ClientAccountRuntimeStartOutcome(
    ConnectionState RealtimeState,
    ClientSyncRunOutcome StartupSyncOutcome)
{
    public bool IsAuthoritativeCacheReady =>
        StartupSyncOutcome.Status == ClientSyncRunStatus.Completed;

    public override string ToString() =>
        $"{nameof(ClientAccountRuntimeStartOutcome)} {{ RealtimeState = {RealtimeState}, " +
        $"StartupSyncOutcome = {StartupSyncOutcome}, " +
        $"IsAuthoritativeCacheReady = {IsAuthoritativeCacheReady} }}";
}
