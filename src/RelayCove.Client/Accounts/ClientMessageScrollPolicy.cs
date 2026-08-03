namespace RelayCove.Client.Accounts;

internal static class ClientMessageScrollPolicy
{
    public static ClientMessageScrollDecision Decide(
        bool sameConversation,
        long? previousOldestMessageId,
        long? previousLatestMessageId,
        long? nextOldestMessageId,
        long? nextLatestMessageId,
        bool wasNearBottom,
        long? targetMessageId,
        bool targetChanged)
    {
        if (targetMessageId is { } target && targetChanged)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: false,
                ScrollToMessageId: target,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: target);
        }

        if (!sameConversation || previousLatestMessageId is null)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: false,
                ScrollToMessageId: nextLatestMessageId,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: nextLatestMessageId);
        }

        var prepended = nextOldestMessageId is { } nextOldest &&
            previousOldestMessageId is { } previousOldest &&
            nextOldest < previousOldest;
        var appended = nextLatestMessageId is { } nextLatest &&
            previousLatestMessageId is { } previousLatest &&
            nextLatest > previousLatest;
        if (prepended)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: true,
                ScrollToMessageId: null,
                ShowNewMessageIndicator: appended,
                ObservedThroughMessageId: null);
        }

        if (appended && wasNearBottom)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: false,
                ScrollToMessageId: nextLatestMessageId,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: nextLatestMessageId);
        }

        return new ClientMessageScrollDecision(
            PreservePrependOffset: false,
            ScrollToMessageId: null,
            ShowNewMessageIndicator: appended,
            ObservedThroughMessageId: null);
    }
}
