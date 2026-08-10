using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class OutboxTimingPolicyTests
{
    [Fact]
    public void Advance_WhenHiddenPastGracePeriod_TransitionsToWaiting()
    {
        var at = DateTimeOffset.UnixEpoch;
        var entry = new OutboxEntry("1", new DirectMessage([]), "hello", at);

        var result = OutboxTimingPolicy.Advance(entry, at + OutboxTimingPolicy.WaitDuration);

        Assert.Equal(OutboxState.Waiting, result.State);
    }

    [Fact]
    public void Advance_WhenWaitingPastTenSeconds_TransitionsToWaitExpired()
    {
        var at = DateTimeOffset.UnixEpoch;
        var entry = new OutboxEntry("2", new DirectMessage([]), "hello", at, OutboxState.Waiting);

        var result = OutboxTimingPolicy.Advance(entry, at + OutboxTimingPolicy.ExpiryDuration);

        Assert.Equal(OutboxState.WaitExpired, result.State);
    }

    [Fact]
    public void MarkFailed_WhenApiRejectsMessage_TransitionsToFailed()
    {
        var entry = new OutboxEntry("3", new DirectMessage([]), "hello", DateTimeOffset.UnixEpoch);

        var failed = OutboxTimingPolicy.MarkFailed(entry, OutboxFailureKind.Rejected);

        Assert.Equal(OutboxState.Failed, failed.State);
        Assert.Equal(OutboxFailureKind.Rejected, failed.Failure);
    }
}
