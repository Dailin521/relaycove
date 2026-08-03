namespace RelayCove.Shared.Errors;

public static class ApiErrorCodes
{
    public const string ValidationFailed = nameof(ValidationFailed);
    public const string AuthenticationFailed = nameof(AuthenticationFailed);
    public const string AuthenticationRequired = nameof(AuthenticationRequired);
    public const string AccessDenied = nameof(AccessDenied);
    public const string SyncCursorInvalid = nameof(SyncCursorInvalid);
    public const string IdempotencyKeyReuse = nameof(IdempotencyKeyReuse);
    public const string ConversationAccessRevoked = nameof(ConversationAccessRevoked);
}
