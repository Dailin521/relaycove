namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageScrollDecision(
    bool PreservePrependOffset,
    long? ScrollToMessageId,
    bool ScrollToEnd,
    bool ShowNewMessageIndicator,
    long? ObservedThroughMessageId);
