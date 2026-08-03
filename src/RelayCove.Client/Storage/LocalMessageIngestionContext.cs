using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalMessageIngestionContext(
    IncomingMessageSource Source,
    Guid? ForegroundConversationId)
{
    public static LocalMessageIngestionContext Background(IncomingMessageSource source) =>
        new(source, ForegroundConversationId: null);

    public bool IsForegroundConversation(Guid conversationId) =>
        ForegroundConversationId == conversationId;

    public override string ToString() =>
        $"{nameof(LocalMessageIngestionContext)} {{ Source = {Source}, " +
        "ForegroundConversationId = [REDACTED] }";
}
