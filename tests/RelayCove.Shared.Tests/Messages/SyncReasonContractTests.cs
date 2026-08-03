using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class SyncReasonContractTests
{
    [Fact]
    public void SyncReason_WhenSerializedAsInteger_HasStableValues()
    {
        Assert.Equal(1, (int)SyncReason.Startup);
        Assert.Equal(2, (int)SyncReason.Reconnect);
        Assert.Equal(3, (int)SyncReason.WindowActivated);
        Assert.Equal(4, (int)SyncReason.Periodic);
    }
}
