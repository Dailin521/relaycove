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
    ReactionAlreadyExists,
    ReactionDoesNotExist,
    ExpectationMismatch,
    ServerError,
    RequestFailed
}
