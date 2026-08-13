namespace RelayCove.Core;

public sealed record MessageMutationState(
    long MessageId,
    MessageMutationKind Kind,
    MessageMutationStatus Status,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"MessageMutationState {{ MessageId = {MessageId}, Kind = {Kind}, Status = {Status}, ErrorCode = {ErrorCode ?? "none"} }}";
}
