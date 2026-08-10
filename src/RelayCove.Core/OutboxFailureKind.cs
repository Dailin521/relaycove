namespace RelayCove.Core;

public enum OutboxFailureKind
{
    Rejected,
    ReauthenticationRequired,
    RateLimited,
    NetworkResultUnknown,
    Protocol
}
