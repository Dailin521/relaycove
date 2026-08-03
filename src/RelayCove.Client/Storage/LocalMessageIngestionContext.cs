using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalMessageIngestionContext(
    IncomingMessageSource Source,
    Guid? ForegroundConversationId,
    bool IsHistoryObservationConfirmed = true)
{
    public static LocalMessageIngestionContext Background(IncomingMessageSource source) =>
        new(source, ForegroundConversationId: null);

    public static LocalMessageIngestionContext UnobservedHistory { get; } =
        new(
            IncomingMessageSource.History,
            ForegroundConversationId: null,
            IsHistoryObservationConfirmed: false);

    public bool IsForegroundConversation(Guid conversationId) =>
        ForegroundConversationId == conversationId;

    public override string ToString() =>
        $"{nameof(LocalMessageIngestionContext)} {{ Source = {Source}, " +
        "ForegroundConversationId = [REDACTED], " +
        $"IsHistoryObservationConfirmed = {IsHistoryObservationConfirmed} }}";
}
