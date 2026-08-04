using RelayCove.Shared.Admin;
using RelayCove.Shared.Conversations;

namespace RelayCove.Client.Admin;

internal sealed record ClientAdminSnapshot(
    bool IsAdmin,
    bool IsBusy,
    ClientAdminRequestStatus? LastStatus,
    IReadOnlyList<AdminUserResponse> Users,
    IReadOnlyList<AdminChannelResponse> Channels,
    ServerStatusResponse? Status,
    UploadSettingsResponse? UploadSettings,
    Guid? SelectedPrivateChannelId,
    IReadOnlyList<ConversationMemberDto> PrivateMembers)
{
    public static ClientAdminSnapshot Hidden { get; } = new(
        false,
        false,
        null,
        Array.Empty<AdminUserResponse>(),
        Array.Empty<AdminChannelResponse>(),
        null,
        null,
        null,
        Array.Empty<ConversationMemberDto>());
}
