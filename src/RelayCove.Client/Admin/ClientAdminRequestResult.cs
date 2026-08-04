namespace RelayCove.Client.Admin;

internal sealed record ClientAdminRequestResult<T>(ClientAdminRequestStatus Status, T? Value)
{
    public static ClientAdminRequestResult<T> Success(T value) =>
        new(ClientAdminRequestStatus.Completed, value);

    public static ClientAdminRequestResult<T> Failure(ClientAdminRequestStatus status) =>
        new(status, default);
}
