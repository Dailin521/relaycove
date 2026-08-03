namespace RelayCove.Client.Auth;

internal enum PersistentClientAuthenticationStatus
{
    Authenticated = 1,
    NoStoredCredential = 2,
    CredentialCorrupt = 3,
    CredentialUnavailable = 4,
    ValidationFailed = 5,
    AuthenticationFailed = 6,
    RateLimited = 7,
    ServiceUnavailable = 8,
    ProtocolError = 9,
    RemoteFailure = 10,
    SessionAlreadyActive = 11,
}
