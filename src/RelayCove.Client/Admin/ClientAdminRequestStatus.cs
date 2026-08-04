namespace RelayCove.Client.Admin;

internal enum ClientAdminRequestStatus
{
    Completed,
    AuthenticationRequired,
    AccessDenied,
    ValidationFailed,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    Canceled,
}
