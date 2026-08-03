using RelayCove.Client.Sync;

namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageListSnapshot(
    ClientMessageListStatus Status,
    Guid? ConversationId,
    IReadOnlyList<ClientMessageListItemPresentation> Messages,
    bool IsLoading,
    bool HasMoreBefore,
    bool HasMoreAfter,
    long? TargetMessageId,
    ClientMessageLoadStatus? LastLoadStatus,
    long Revision = 0)
{
    public static ClientMessageListSnapshot Initial { get; } = new(
        ClientMessageListStatus.None,
        ConversationId: null,
        Array.Empty<ClientMessageListItemPresentation>(),
        IsLoading: false,
        HasMoreBefore: false,
        HasMoreAfter: false,
        TargetMessageId: null,
        LastLoadStatus: null);

    public bool CanLoadOlder =>
        Status == ClientMessageListStatus.Ready &&
        !IsLoading &&
        HasMoreBefore;

    public long? LatestMessageId => Messages
        .Select(message => message.ServerMessageId)
        .LastOrDefault(messageId => messageId.HasValue);

    public override string ToString() =>
        $"{nameof(ClientMessageListSnapshot)} {{ Status = {Status}, " +
        "ConversationId = [REDACTED], Messages = [REDACTED], " +
        $"IsLoading = {IsLoading}, HasMoreBefore = {HasMoreBefore}, " +
        $"HasMoreAfter = {HasMoreAfter}, TargetMessageId = [REDACTED], " +
        $"LastLoadStatus = {LastLoadStatus}, Revision = {Revision} }}";
}
