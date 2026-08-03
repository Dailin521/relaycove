namespace RelayCove.Server.Services;

public enum MessageOperationStatus
{
    Created,
    Replay,
    Success,
    InvalidRequest,
    AccessRevoked,
    MessageTypeUnsupported,
    ReplyInvalid,
    MentionInvalid,
    ReadTargetInvalid,
    IdempotencyKeyReuse,
}
