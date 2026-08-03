namespace RelayCove.Client.Auth;

public sealed class ClientLoginOutcome
{
    private ClientLoginOutcome(
        ClientLoginStatus status,
        ClientAuthenticationSession? session,
        TimeSpan? retryAfter)
    {
        Status = status;
        Session = session;
        RetryAfter = retryAfter;
    }

    public ClientLoginStatus Status { get; }

    public ClientAuthenticationSession? Session { get; }

    public TimeSpan? RetryAfter { get; }

    public override string ToString() =>
        $"{nameof(ClientLoginOutcome)} {{ Status = {Status}, Session = [REDACTED], " +
        $"RetryAfter = {RetryAfter} }}";

    internal static ClientLoginOutcome Authenticated(ClientAuthenticationSession session) =>
        new(ClientLoginStatus.Authenticated, session, retryAfter: null);

    internal static ClientLoginOutcome Failure(
        ClientLoginStatus status,
        TimeSpan? retryAfter = null) =>
        new(status, session: null, retryAfter);
}
