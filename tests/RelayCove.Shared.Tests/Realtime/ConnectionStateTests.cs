using RelayCove.Shared.Realtime;

namespace RelayCove.Shared.Tests.Realtime;

public sealed class ConnectionStateTests
{
    [Fact]
    public void Values_WhenEnumerated_HaveStableWireNumbers()
    {
        Assert.Equal(0, (int)ConnectionState.Disconnected);
        Assert.Equal(1, (int)ConnectionState.Connecting);
        Assert.Equal(2, (int)ConnectionState.Connected);
        Assert.Equal(3, (int)ConnectionState.Reconnecting);
        Assert.Equal(4, (int)ConnectionState.ServerUnavailable);
    }
}
