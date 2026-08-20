namespace RelayCove.Core.Tests;

public sealed class ChannelPermissionEvaluatorTests
{
    [Fact]
    public void IsMember_WhenNestedAndAnonymousGroupsContainUser_ReturnsTrue()
    {
        var groups = new[] { new ChannelUserGroup(10, "parent", false, [], [11]), new ChannelUserGroup(11, "child", false, [7], []) };

        Assert.True(ChannelPermissionEvaluator.IsMember(7, new NamedChannelGroupSetting(10), groups));
        Assert.True(ChannelPermissionEvaluator.IsMember(7, new AnonymousChannelGroupSetting([], [10]), groups));
        Assert.True(ChannelPermissionEvaluator.IsMember(7, new AnonymousChannelGroupSetting([7], []), groups));
    }

    [Fact]
    public void IsMember_WhenGroupsAreMissingDeactivatedOrCyclic_FailsClosed()
    {
        var groups = new[]
        {
            new ChannelUserGroup(10, "one", false, [], [11]),
            new ChannelUserGroup(11, "two", false, [], [10]),
            new ChannelUserGroup(12, "off", true, [7], [])
        };

        Assert.False(ChannelPermissionEvaluator.IsMember(7, new NamedChannelGroupSetting(10), groups));
        Assert.False(ChannelPermissionEvaluator.IsMember(7, new NamedChannelGroupSetting(12), groups));
        Assert.False(ChannelPermissionEvaluator.IsMember(7, new NamedChannelGroupSetting(99), groups));
    }

    [Fact]
    public void IsMember_WhenNestedGroupsFormSharedDag_ResolvesEachGroupDeterministically()
    {
        var groups = new[]
        {
            new ChannelUserGroup(10, "root", false, [], [11, 12]),
            new ChannelUserGroup(11, "left", false, [], [13]),
            new ChannelUserGroup(12, "right", false, [], [13, 14]),
            new ChannelUserGroup(13, "shared", false, [], []),
            new ChannelUserGroup(14, "member", false, [7], [])
        };

        Assert.True(ChannelPermissionEvaluator.IsMember(7, new NamedChannelGroupSetting(10), groups));
        Assert.True(ChannelPermissionEvaluator.IsMember(7, new AnonymousChannelGroupSetting([], [11, 12]), groups));
    }

    [Fact]
    public void Evaluate_WhenCurrentUserIsOrganizationAdministrator_GrantsAdministration()
    {
        var channel = new ChannelDetails(42, "general", null, false, false, false, null, null, null, null, null, new NamedChannelGroupSetting(99));
        var snapshot = new ChannelSettingsSnapshot([new ChannelSummary(42, "general", null, false, null)], [], [], 7, true, false, new ChannelSettingsLimits(null, null, null, null));

        var result = ChannelPermissionEvaluator.Evaluate(channel, snapshot, false);

        Assert.True(result.CanAdministerChannel);
        Assert.False(result.CanSendMessages);
    }

    [Fact]
    public void Evaluate_WhenPrivateChannelAdministratorIsNotSubscribed_DoesNotGrantContentAccess()
    {
        var groups = new[]
        {
            new ChannelUserGroup(10, "channel-admins", false, [7], []),
            new ChannelUserGroup(11, "senders", false, [7], [])
        };
        var channel = new ChannelDetails(
            42,
            "private",
            null,
            false,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            new NamedChannelGroupSetting(10),
            CanSendMessageGroup: new NamedChannelGroupSetting(11));
        var snapshot = new ChannelSettingsSnapshot(
            [new ChannelSummary(42, "private", null, false, null, IsPrivate: true)],
            [],
            groups,
            7,
            false,
            false,
            new ChannelSettingsLimits(null, null, null, null));

        var result = ChannelPermissionEvaluator.Evaluate(channel, snapshot, false);

        Assert.True(result.CanAdministerChannel);
        Assert.False(result.HasContentAccess);
        Assert.False(result.CanSendMessages);
        Assert.True(result.CanRemoveSubscribers);
    }

    [Fact]
    public void Evaluate_WhenAddAndRemoveGroupsArePresent_GrantsOnlyTheirScopedCapabilities()
    {
        var groups = new[]
        {
            new ChannelUserGroup(10, "add", false, [7], []),
            new ChannelUserGroup(11, "remove", false, [7], [])
        };
        var channel = new ChannelDetails(42, "general", null, false, false, false, null, null, null, null, null, null,
            CanAddSubscribersGroup: new NamedChannelGroupSetting(10),
            CanRemoveSubscribersGroup: new NamedChannelGroupSetting(11));
        var snapshot = new ChannelSettingsSnapshot([new ChannelSummary(42, "general", null, false, null)], [], groups, 7, false, false, new ChannelSettingsLimits(null, null, null, null));

        var result = ChannelPermissionEvaluator.Evaluate(channel, snapshot, true);

        Assert.True(result.CanAddSubscribers);
        Assert.True(result.CanRemoveSubscribers);
    }

    [Fact]
    public void Evaluate_WhenSubscribedToPrivateChannelAndInRemoveGroup_GrantsRemoval()
    {
        var groups = new[] { new ChannelUserGroup(11, "remove", false, [7], []) };
        var channel = new ChannelDetails(
            42,
            "private",
            null,
            false,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CanRemoveSubscribersGroup: new NamedChannelGroupSetting(11));
        var snapshot = new ChannelSettingsSnapshot(
            [new ChannelSummary(42, "private", null, false, null, IsPrivate: true, IsSubscribed: true)],
            [],
            groups,
            7,
            false,
            false,
            new ChannelSettingsLimits(null, null, null, null));

        var result = ChannelPermissionEvaluator.Evaluate(channel, snapshot, true);

        Assert.True(result.HasContentAccess);
        Assert.True(result.CanRemoveSubscribers);
    }
}
