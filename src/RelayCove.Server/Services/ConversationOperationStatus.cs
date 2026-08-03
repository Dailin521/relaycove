namespace RelayCove.Server.Services;

public enum ConversationOperationStatus
{
    Created,
    Success,
    NoContent,
    InvalidRequest,
    AccessDenied,
    AccessRevoked,
    UserNotFound,
    ConversationTypeConflict,
}
