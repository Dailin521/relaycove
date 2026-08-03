using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class LocalMessageEnumContractTests
{
    [Fact]
    public void MessageSendStatus_HasStableWireValues()
    {
        Assert.Equal(1, (int)MessageSendStatus.Sending);
        Assert.Equal(2, (int)MessageSendStatus.Sent);
        Assert.Equal(3, (int)MessageSendStatus.Failed);
    }

    [Fact]
    public void IncomingMessageMergeResult_HasStableValues()
    {
        Assert.Equal(1, (int)IncomingMessageMergeResult.Inserted);
        Assert.Equal(2, (int)IncomingMessageMergeResult.PendingPromoted);
        Assert.Equal(3, (int)IncomingMessageMergeResult.Duplicate);
        Assert.Equal(4, (int)IncomingMessageMergeResult.Conflict);
    }
}
