using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal interface IClientAccountRealtimeConnection : IAsyncDisposable
{
    ConnectionState State { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}
