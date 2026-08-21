namespace RelayCove.Core;

public static class PrivateGroupPolicy
{
    public static bool IsEligible(Subscription? subscription) =>
        subscription is
        {
            IsActive: true,
            IsPrivate: true,
            IsWebPublic: false,
            TopicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly
        };

    public static bool IsEligible(ChannelDetails? details) =>
        details is
        {
            IsArchived: false,
            IsPrivate: true,
            IsWebPublic: false,
            TopicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly
        };

    public static long? TryGetOwnerId(ChannelDetails? details)
    {
        if (!IsEligible(details)) return null;
        var administer = GetSingleDirectMember(details!.CanAdministerChannelGroup);
        var add = GetSingleDirectMember(details.CanAddSubscribersGroup);
        var remove = GetSingleDirectMember(details.CanRemoveSubscribersGroup);
        return administer is > 0 && administer == add && administer == remove
            ? administer
            : null;
    }

    public static AnonymousChannelGroupSetting OwnerGroup(long userId)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        return new AnonymousChannelGroupSetting([userId], []);
    }

    private static long? GetSingleDirectMember(ChannelGroupSetting? setting)
    {
        if (setting is not AnonymousChannelGroupSetting
            {
                DirectMembers.Count: 1,
                DirectSubgroups.Count: 0
            } anonymous || anonymous.DirectMembers[0] <= 0)
        {
            return null;
        }

        return anonymous.DirectMembers[0];
    }
}
