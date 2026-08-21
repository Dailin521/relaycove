namespace RelayCove.Core.Tests;

public sealed class PrivateGroupPolicyTests
{
    [Theory]
    [InlineData(true, false, ChannelTopicsPolicy.EmptyTopicOnly, true)]
    [InlineData(true, true, ChannelTopicsPolicy.EmptyTopicOnly, false)]
    [InlineData(false, false, ChannelTopicsPolicy.EmptyTopicOnly, false)]
    [InlineData(true, false, ChannelTopicsPolicy.Inherit, false)]
    public void IsEligible_WhenSubscriptionMetadataChanges_FailsClosed(
        bool isPrivate,
        bool isWebPublic,
        ChannelTopicsPolicy topicsPolicy,
        bool expected)
    {
        var subscription = new Subscription(
            7,
            "group",
            isPrivate: isPrivate,
            topicsPolicy: topicsPolicy,
            isWebPublic: isWebPublic);

        Assert.Equal(expected, PrivateGroupPolicy.IsEligible(subscription));
    }

    [Fact]
    public void TryGetOwnerId_WhenThreeDirectGroupsAgree_ReturnsOwner()
    {
        var owner = PrivateGroupPolicy.OwnerGroup(10);
        var details = CreateDetails(owner, owner, owner);

        Assert.Equal(10, PrivateGroupPolicy.TryGetOwnerId(details));
    }

    [Fact]
    public void TryGetOwnerId_WhenGroupsAreComplexOrDisagree_FailsClosed()
    {
        var directOwner = PrivateGroupPolicy.OwnerGroup(10);
        var differentOwner = PrivateGroupPolicy.OwnerGroup(20);
        var nested = new AnonymousChannelGroupSetting([10], [4]);

        Assert.Null(PrivateGroupPolicy.TryGetOwnerId(CreateDetails(directOwner, directOwner, differentOwner)));
        Assert.Null(PrivateGroupPolicy.TryGetOwnerId(CreateDetails(nested, nested, nested)));
        Assert.Null(PrivateGroupPolicy.TryGetOwnerId(CreateDetails(new NamedChannelGroupSetting(4), new NamedChannelGroupSetting(4), new NamedChannelGroupSetting(4))));
    }

    private static ChannelDetails CreateDetails(
        ChannelGroupSetting administer,
        ChannelGroupSetting add,
        ChannelGroupSetting remove) => new(
        7,
        "group",
        string.Empty,
        false,
        true,
        false,
        null,
        null,
        null,
        10,
        null,
        administer,
        add,
        HistoryPublicToSubscribers: true,
        TopicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly,
        CanRemoveSubscribersGroup: remove);
}
