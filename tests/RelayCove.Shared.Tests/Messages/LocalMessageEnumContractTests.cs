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

    [Fact]
    public void IncomingMessageSource_HasStableValues()
    {
        Assert.Equal(1, (int)IncomingMessageSource.Realtime);
        Assert.Equal(2, (int)IncomingMessageSource.Sync);
        Assert.Equal(3, (int)IncomingMessageSource.History);
        Assert.Equal(4, (int)IncomingMessageSource.SendResponse);
    }
}
