namespace RelayCove.App.ViewModels;

public sealed record MessageScrollRequest(
    long Sequence,
    string ConversationKey,
    long Generation,
    long TargetMessageId,
    MessageScrollReason Reason);
