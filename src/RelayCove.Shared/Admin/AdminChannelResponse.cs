using RelayCove.Shared.Conversations;

namespace RelayCove.Shared.Admin;

public sealed record AdminChannelResponse(
    Guid Id,
    ConversationType Type,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
