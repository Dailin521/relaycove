namespace RelayCove.Shared.Messages;

public sealed record MarkConversationReadRequest(long MessageId)
{
    public override string ToString() =>
        $"{nameof(MarkConversationReadRequest)} {{ MessageId = [REDACTED] }}";
}
