namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageScrollDecision(
    bool PreservePrependOffset,
    long? ScrollToMessageId,
    bool ShowNewMessageIndicator,
    long? ObservedThroughMessageId);
