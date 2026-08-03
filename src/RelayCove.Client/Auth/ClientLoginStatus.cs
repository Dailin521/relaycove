namespace RelayCove.Client.Auth;

public enum ClientLoginStatus
{
    Authenticated = 1,
    ValidationFailed = 2,
    AuthenticationFailed = 3,
    RateLimited = 4,
    ServiceUnavailable = 5,
    ProtocolError = 6,
    RemoteFailure = 7,
    StoredIdentityMismatch = 8,
}
