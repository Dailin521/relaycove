namespace RelayCove.Server.Services;

public enum AdminUserMutationStatus
{
    Updated,
    Unchanged,
    PasswordReset,
    Retired,
    ValidationFailed,
    ActorNotAdministrator,
    UserNotFound,
    UserRetired,
    SelfActionForbidden,
    LastActiveAdministrator,
}
