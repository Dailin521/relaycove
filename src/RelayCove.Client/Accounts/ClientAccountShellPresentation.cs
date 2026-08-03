namespace RelayCove.Client.Accounts;

internal sealed record ClientAccountShellPresentation(
    bool ShowLogin,
    bool IsBusy,
    string Heading,
    string Detail,
    string DisplayName,
    string ServerAddress,
    string ConnectionLabel,
    string SyncLabel,
    bool CanRetry,
    bool CanLogout)
{
    public override string ToString() =>
        $"{nameof(ClientAccountShellPresentation)} {{ ShowLogin = {ShowLogin}, " +
        $"IsBusy = {IsBusy}, Heading = {Heading}, Detail = {Detail}, " +
        "DisplayName = [REDACTED], ServerAddress = [REDACTED], " +
        $"ConnectionLabel = {ConnectionLabel}, SyncLabel = {SyncLabel}, " +
        $"CanRetry = {CanRetry}, CanLogout = {CanLogout} }}";
}
