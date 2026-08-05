namespace RelayCove.Server.Services;

public sealed record WebAdminLoginResult(Guid UserId, string DisplayName, long AccessTokenVersion);
