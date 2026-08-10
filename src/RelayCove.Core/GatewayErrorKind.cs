namespace RelayCove.Core;

public enum GatewayErrorKind
{
    AuthenticationFailed,
    ReauthRequired,
    RateLimited,
    Offline,
    QueueExpired,
    IncompatibleRealm,
    Protocol,
    Server,
    RequestFailed
}
