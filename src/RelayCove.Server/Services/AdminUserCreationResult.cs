using RelayCove.Shared.Admin;

namespace RelayCove.Server.Services;

public sealed record AdminUserCreationResult(
    AdminUserCreationStatus Status,
    AdminUserResponse? User = null);
