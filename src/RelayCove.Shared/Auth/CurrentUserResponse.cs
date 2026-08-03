namespace RelayCove.Shared.Auth;

public sealed record CurrentUserResponse(
    Guid UserId,
    string UserName,
    string DisplayName,
    bool IsAdmin);
