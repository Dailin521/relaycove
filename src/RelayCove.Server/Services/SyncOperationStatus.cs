namespace RelayCove.Server.Services;

public enum SyncOperationStatus
{
    Success,
    InvalidRequest,
    CursorInvalid,
    AuthenticationUnavailable,
}
