namespace RelayCove.Shared.Messages;

public sealed record MessageAroundResponse(
    IReadOnlyList<MessageDto> Messages,
    long TargetMessageId,
    bool HasMoreBefore,
    bool HasMoreAfter)
{
    public override string ToString() =>
        $"{nameof(MessageAroundResponse)} {{ Messages = [REDACTED], " +
        "TargetMessageId = [REDACTED], " +
        $"HasMoreBefore = {HasMoreBefore}, HasMoreAfter = {HasMoreAfter} }}";
}
