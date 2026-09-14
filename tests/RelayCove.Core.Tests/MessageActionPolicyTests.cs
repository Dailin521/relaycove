using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class MessageActionPolicyTests
{
    private static readonly DateTimeOffset Sent = DateTimeOffset.UnixEpoch;
    private static readonly ChatMessage Message = new(1, new DirectMessage([8]), 7, "hello", Sent);

    [Theory]
    [InlineData(599, true)]
    [InlineData(600, false)]
    [InlineData(601, false)]
    public void CanChange_WhenServerLimitsAreMissing_UsesTenMinuteBoundary(int age, bool expected)
    {
        var policy = new MessageActionPolicy();
        Assert.Equal(expected, policy.CanEdit(Message, 7, Sent.AddSeconds(age)));
        Assert.Equal(expected, policy.CanDelete(Message, 7, Sent.AddSeconds(age)));
    }

    [Theory]
    [InlineData(59, true, true)]
    [InlineData(60, false, true)]
    [InlineData(120, false, false)]
    public void CanChange_WhenServerLimitsDiffer_EvaluatesEachAction(int age, bool edit, bool delete)
    {
        var policy = new MessageActionPolicy { EditLimitSeconds = 60, DeleteLimitSeconds = 120 };
        Assert.Equal(edit, policy.CanEdit(Message, 7, Sent.AddSeconds(age)));
        Assert.Equal(delete, policy.CanDelete(Message, 7, Sent.AddSeconds(age)));
    }

    [Fact]
    public void CanChange_WhenServerDeclaresUnlimited_DoesNotApplyFallback()
    {
        var policy = new MessageActionPolicy { EditLimitSeconds = null, DeleteLimitSeconds = null };
        Assert.True(policy.CanEdit(Message, 7, Sent.AddYears(10)));
        Assert.True(policy.CanDelete(Message, 7, Sent.AddYears(10)));
        Assert.False(policy.CanEdit(Message, 8, Sent));
        Assert.False(policy.CanDelete(Message, 8, Sent));
    }

    [Fact]
    public void CanChange_WhenPermissionIsDenied_HidesEvenRecentMessages()
    {
        Assert.False(MessageActionPolicy.Unavailable.CanEdit(Message, 7, Sent));
        Assert.False(MessageActionPolicy.Unavailable.CanDelete(Message, 7, Sent));
        Assert.False(new MessageActionPolicy { AllowEditing = false }.CanEdit(Message, 7, Sent));
        Assert.False(new MessageActionPolicy { CanDeleteOwn = false }.CanDelete(Message, 7, Sent));
    }

    [Fact]
    public void CanDelete_WhenDeleteAnyIsGranted_IgnoresTimeLimitButRetainsOwnMessageScope()
    {
        var policy = new MessageActionPolicy { CanDeleteOwn = false, CanDeleteAny = true };
        Assert.True(policy.CanDelete(Message, 7, Sent.AddYears(10)));
        Assert.False(policy.CanDelete(Message, 8, Sent));
    }

    [Theory]
    [InlineData(false, true, false, 1, true)]
    [InlineData(false, true, false, 600, false)]
    [InlineData(false, false, true, 1000, true)]
    [InlineData(true, true, true, 1, false)]
    public void CanDelete_WhenChannelGrantsPermission_CombinesWithRealmRules(
        bool archived, bool own, bool any, int age, bool expected)
    {
        var message = Message with { Conversation = new ChannelTopic(10, "topic") };
        var policy = new MessageActionPolicy
        {
            CanDeleteOwn = false,
            Channels = new Dictionary<long, MessageActionChannelPolicy> { [10] = new(archived, own, any) }
        };
        Assert.Equal(expected, policy.CanDelete(message, 7, Sent.AddSeconds(age)));
        Assert.Equal(!archived, policy.CanEdit(message, 7, Sent));
    }

    [Fact]
    public void Apply_WhenPolicyIsInvalidated_DeniesActionsAndPreservesOtherState()
    {
        var state = new ClientState(messages: new Dictionary<long, ChatMessage> { [1] = Message },
            messageActions: new MessageActionPolicy(), lastEventId: 1);
        state = DomainReducer.Apply(state, new HeartbeatEvent(2));
        Assert.True(state.MessageActions.CanEdit(Message, 7, Sent));
        state = DomainReducer.Apply(state, new MessageActionPolicyInvalidatedEvent(3));
        Assert.False(state.MessageActions.IsAvailable);
        Assert.Single(state.Messages);
        Assert.Equal(3, state.LastEventId);
    }
}
