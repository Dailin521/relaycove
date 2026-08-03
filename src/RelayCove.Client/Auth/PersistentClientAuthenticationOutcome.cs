namespace RelayCove.Client.Auth;

internal sealed class PersistentClientAuthenticationOutcome
{
    private PersistentClientAuthenticationOutcome(
        PersistentClientAuthenticationStatus status,
        ClientAuthenticationSession? session,
        bool isCredentialPersisted,
        TimeSpan? retryAfter)
    {
        Status = status;
        Session = session;
        IsCredentialPersisted = isCredentialPersisted;
        RetryAfter = retryAfter;
    }

    public PersistentClientAuthenticationStatus Status { get; }

    public ClientAuthenticationSession? Session { get; }

    public bool IsCredentialPersisted { get; }

    public TimeSpan? RetryAfter { get; }

    public override string ToString() =>
        $"{nameof(PersistentClientAuthenticationOutcome)} {{ Status = {Status}, " +
        $"Session = [REDACTED], IsCredentialPersisted = {IsCredentialPersisted}, " +
        $"RetryAfter = {RetryAfter} }}";

    internal static PersistentClientAuthenticationOutcome Authenticated(
        ClientAuthenticationSession session,
        bool isCredentialPersisted) =>
        new(
            PersistentClientAuthenticationStatus.Authenticated,
            session,
            isCredentialPersisted,
            retryAfter: null);

    internal static PersistentClientAuthenticationOutcome Failure(
        PersistentClientAuthenticationStatus status,
        TimeSpan? retryAfter = null) =>
        new(status, session: null, isCredentialPersisted: false, retryAfter);
}
