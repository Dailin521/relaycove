namespace RelayCove.Shared.Messages;

public sealed record ConversationReadReceipt(
    Guid ConversationId,
    long LastReadMessageId)
{
    public override string ToString() =>
        $"{nameof(ConversationReadReceipt)} {{ ConversationId = {ConversationId}, " +
        "LastReadMessageId = [REDACTED] }";
}
