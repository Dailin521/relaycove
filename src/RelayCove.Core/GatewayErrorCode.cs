namespace RelayCove.Core;

public enum GatewayErrorCode
{
    AuthenticationFailed,
    Unauthorized,
    RateLimited,
    NetworkError,
    RequestTimedOut,
    BadEventQueueId,
    RedirectNotAllowed,
    IncompatibleRealm,
    InvalidResponse,
    ServerError,
    RequestFailed
}
