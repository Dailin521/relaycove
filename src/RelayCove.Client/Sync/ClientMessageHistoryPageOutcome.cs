using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientMessageHistoryPageOutcome(
    ClientMessageLoadStatus Status,
    IReadOnlyList<MessageDto> Messages,
    long? NextBeforeMessageId,
    bool HasMore)
{
    public static ClientMessageHistoryPageOutcome Failure(
        ClientMessageLoadStatus status) =>
        new(
            status,
            Array.Empty<MessageDto>(),
            NextBeforeMessageId: null,
            HasMore: false);

    public override string ToString() =>
        $"{nameof(ClientMessageHistoryPageOutcome)} {{ Status = {Status}, " +
        "Messages = [REDACTED], NextBeforeMessageId = [REDACTED], " +
        $"HasMore = {HasMore} }}";
}
