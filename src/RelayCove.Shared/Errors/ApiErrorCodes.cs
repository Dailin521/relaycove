namespace RelayCove.Shared.Errors;

public static class ApiErrorCodes
{
    public const string ValidationFailed = "ValidationFailed";
    public const string AuthenticationFailed = "AuthenticationFailed";
    public const string AuthenticationRequired = "AuthenticationRequired";
    public const string AccessDenied = "AccessDenied";
    public const string RateLimitExceeded = "RateLimitExceeded";
    public const string ServiceUnavailable = "ServiceUnavailable";
    public const string InternalServerError = "InternalServerError";
    public const string UserNameAlreadyExists = "UserNameAlreadyExists";
    public const string UserNotFound = "UserNotFound";
    public const string ConversationTypeConflict = "ConversationTypeConflict";
    public const string SyncCursorInvalid = "SyncCursorInvalid";
    public const string IdempotencyKeyReuse = "IdempotencyKeyReuse";
    public const string ConversationAccessRevoked = "ConversationAccessRevoked";
}
