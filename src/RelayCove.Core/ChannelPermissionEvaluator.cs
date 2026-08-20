namespace RelayCove.Core;

/// <summary>Pure, fail-closed evaluator for the Zulip channel group-setting model.</summary>
public static class ChannelPermissionEvaluator
{
    public static ChannelSettingsAccess Evaluate(
        ChannelDetails channel,
        ChannelSettingsSnapshot snapshot,
        bool isSubscribed)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(snapshot);
        var userId = snapshot.CurrentUserId;
        var administerGroup = IsMember(userId, channel.CanAdministerChannelGroup, snapshot.UserGroups);
        var addSubscribersGroup = IsMember(userId, channel.CanAddSubscribersGroup, snapshot.UserGroups);
        var removeSubscribersGroup = IsMember(userId, channel.CanRemoveSubscribersGroup, snapshot.UserGroups);
        var subscribeGroup = IsMember(userId, channel.CanSubscribeGroup, snapshot.UserGroups);
        var sendGroup = IsMember(userId, channel.CanSendMessageGroup, snapshot.UserGroups);
        var createTopicGroup = IsMember(userId, channel.CanCreateTopicGroup, snapshot.UserGroups);
        var administrator = snapshot.IsOrganizationAdministrator || administerGroup;
        var metadata = snapshot.Channels.Any(item => item.ChannelId == channel.ChannelId) &&
            (channel.IsWebPublic || !snapshot.IsGuest && !channel.IsPrivate || isSubscribed || administrator || addSubscribersGroup || subscribeGroup);
        var canAdminister = metadata && administrator;
        var content = isSubscribed || !snapshot.IsGuest && !channel.IsPrivate || subscribeGroup;
        var canSubscribe = !channel.IsArchived && !isSubscribed && (!snapshot.IsGuest && !channel.IsPrivate || subscribeGroup);
        var canSend = !channel.IsArchived && content && sendGroup;
        var canCreateTopics = canSend && createTopicGroup;
        var canAddSubscribers = metadata && (administrator || addSubscribersGroup);
        var canRemoveSubscribers = administrator || content && removeSubscribersGroup;
        return new ChannelSettingsAccess(userId, snapshot.IsOrganizationAdministrator, snapshot.IsGuest, metadata, content, canAdminister, canSubscribe, canSend, canCreateTopics, canAddSubscribers, canRemoveSubscribers);
    }

    public static bool IsMember(long userId, ChannelGroupSetting? setting, IReadOnlyList<ChannelUserGroup> groups)
    {
        if (userId <= 0 || setting is null) return false;
        var byId = groups.Where(group => group.GroupId > 0 && !group.IsDeactivated).ToDictionary(group => group.GroupId);
        var visiting = new HashSet<long>();
        var memo = new Dictionary<long, bool>();
        return IsMemberCore(userId, setting, byId, visiting, memo);
    }

    private static bool IsMemberCore(
        long userId,
        ChannelGroupSetting setting,
        IReadOnlyDictionary<long, ChannelUserGroup> groups,
        ISet<long> visiting,
        IDictionary<long, bool> memo)
    {
        if (setting is AnonymousChannelGroupSetting anonymous)
        {
            return anonymous.DirectMembers.Contains(userId) ||
                anonymous.DirectSubgroups.Any(id => IsGroupMember(userId, id, groups, visiting, memo));
        }
        return setting is NamedChannelGroupSetting named &&
            IsGroupMember(userId, named.GroupId, groups, visiting, memo);
    }

    private static bool IsGroupMember(
        long userId,
        long groupId,
        IReadOnlyDictionary<long, ChannelUserGroup> groups,
        ISet<long> visiting,
        IDictionary<long, bool> memo)
    {
        if (memo.TryGetValue(groupId, out var cached)) return cached;
        if (!groups.TryGetValue(groupId, out var group) || !visiting.Add(groupId)) return false;
        var result = group.Members.Contains(userId) ||
            group.DirectSubgroupIds.Any(id => IsGroupMember(userId, id, groups, visiting, memo));
        visiting.Remove(groupId);
        memo[groupId] = result;
        return result;
    }
}
