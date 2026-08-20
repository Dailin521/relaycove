namespace RelayCove.Core;

public sealed record ChannelSettingsAccess(
    long CurrentUserId,
    bool IsOrganizationAdministrator,
    bool IsGuest,
    bool HasMetadataAccess,
    bool HasContentAccess,
    bool CanAdministerChannel,
    bool CanSubscribe,
    bool CanSendMessages,
    bool CanCreateTopics,
    bool CanAddSubscribers = false,
    bool CanRemoveSubscribers = false)
{
    public static readonly ChannelSettingsAccess ReadOnly = new(0, false, true, false, false, false, false, false, false, false, false);
}
