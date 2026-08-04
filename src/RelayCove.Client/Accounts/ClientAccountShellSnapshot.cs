using RelayCove.Client.Auth;
using RelayCove.Client.Sync;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed record ClientAccountShellSnapshot(
    ClientAccountShellPhase Phase,
    PersistentClientAuthenticationStatus? AuthenticationStatus,
    string? DisplayName,
    Uri? ServerBaseUri,
    ConnectionState ConnectionState,
    ClientSyncRunStatus? LastSyncStatus,
    ClientLogoutStatus? LastLogoutStatus,
    TimeSpan? RetryAfter,
    int TotalUnreadCount = 0,
    long Revision = 0,
    bool IsAdmin = false)
{
    public static ClientAccountShellSnapshot Initial { get; } = SignedOut(
        PersistentClientAuthenticationStatus.NoStoredCredential);

    public bool HasActiveAccount =>
        Phase is ClientAccountShellPhase.Active or ClientAccountShellPhase.Retrying;

    public static ClientAccountShellSnapshot SignedOut(
        PersistentClientAuthenticationStatus? status = null,
        ClientLogoutStatus? logoutStatus = null,
        TimeSpan? retryAfter = null) =>
        new(
            ClientAccountShellPhase.SignedOut,
            status,
            DisplayName: null,
            ServerBaseUri: null,
            ConnectionState.Disconnected,
            LastSyncStatus: null,
            logoutStatus,
            retryAfter);

    public override string ToString() =>
        $"{nameof(ClientAccountShellSnapshot)} {{ Phase = {Phase}, " +
        $"AuthenticationStatus = {AuthenticationStatus}, DisplayName = [REDACTED], " +
        $"ServerBaseUri = [REDACTED], ConnectionState = {ConnectionState}, " +
        $"LastSyncStatus = {LastSyncStatus}, LastLogoutStatus = {LastLogoutStatus}, " +
        $"RetryAfter = {RetryAfter}, TotalUnreadCount = {TotalUnreadCount}, " +
        $"Revision = {Revision} }}";
}
