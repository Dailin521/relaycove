namespace RelayCove.Client.Updates;

internal enum ClientUpdateFetchStatus
{
    Success,
    TransientFailure,
    ProtocolError,
    RemoteFailure,
    Canceled,
}
