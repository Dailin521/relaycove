namespace RelayCove.Shared.Admin;

public sealed record AdminUserResponse(
    Guid UserId,
    string UserName,
    string DisplayName,
    bool IsAdmin,
    bool IsDisabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RetiredAt);
