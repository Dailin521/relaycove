using RelayCove.Shared.Users;

namespace RelayCove.Shared.Conversations;

public sealed record ConversationParticipantListResponse(
    Guid ConversationId,
    ConversationType Type,
    bool CanManageMembers,
    IReadOnlyList<UserDirectoryEntryDto> Participants)
{
    public override string ToString() =>
        $"{nameof(ConversationParticipantListResponse)} {{ ConversationId = [REDACTED], " +
        $"Type = {Type}, CanManageMembers = {CanManageMembers}, " +
        "Participants = [REDACTED] }";
}
