namespace RelayCove.Client.Storage;

internal sealed record LocalReadThroughUploadTarget(
    Guid ConversationId,
    long RawPendingMessageId,
    long SafeMessageId)
{
    public override string ToString() =>
        $"{nameof(LocalReadThroughUploadTarget)} {{ ConversationId = [REDACTED], " +
        "RawPendingMessageId = [REDACTED], SafeMessageId = [REDACTED] }";
}
