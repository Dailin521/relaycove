using RelayCove.Shared.Admin;

namespace RelayCove.Server.Services;

public sealed record AdminUserMutationResult(
    AdminUserMutationStatus Status,
    AdminUserResponse? User = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    long? MinimumAccessTokenVersion = null)
{
    public bool RequiresAccessRevocation => Status is
        AdminUserMutationStatus.Updated or
        AdminUserMutationStatus.PasswordReset or
        AdminUserMutationStatus.Retired;
}
