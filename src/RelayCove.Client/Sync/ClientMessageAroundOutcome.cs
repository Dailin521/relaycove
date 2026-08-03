using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientMessageAroundOutcome(
    ClientMessageLoadStatus Status,
    IReadOnlyList<MessageDto> Messages,
    long? TargetMessageId,
    bool HasMoreBefore,
    bool HasMoreAfter)
{
    public static ClientMessageAroundOutcome Failure(
        ClientMessageLoadStatus status) =>
        new(
            status,
            Array.Empty<MessageDto>(),
            TargetMessageId: null,
            HasMoreBefore: false,
            HasMoreAfter: false);

    public override string ToString() =>
        $"{nameof(ClientMessageAroundOutcome)} {{ Status = {Status}, " +
        "Messages = [REDACTED], TargetMessageId = [REDACTED], " +
        $"HasMoreBefore = {HasMoreBefore}, HasMoreAfter = {HasMoreAfter} }}";
}
