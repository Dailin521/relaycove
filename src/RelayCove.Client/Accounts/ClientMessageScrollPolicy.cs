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
        bool targetChanged,
        bool contentAppended = false,
        bool hasNextItems = true)
    {
        if (targetMessageId is { } target && targetChanged)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: false,
                ScrollToMessageId: target,
                ScrollToEnd: false,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: target);
        }

        if (!sameConversation || previousLatestMessageId is null)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: false,
                ScrollToMessageId: nextLatestMessageId,
                ScrollToEnd: nextLatestMessageId is null && hasNextItems,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: nextLatestMessageId);
        }

        var prepended = nextOldestMessageId is { } nextOldest &&
            previousOldestMessageId is { } previousOldest &&
            nextOldest < previousOldest;
        var appended = contentAppended ||
            (nextLatestMessageId is { } nextLatest &&
            previousLatestMessageId is { } previousLatest &&
            nextLatest > previousLatest);
        var sameWindow = previousOldestMessageId == nextOldestMessageId &&
            previousLatestMessageId == nextLatestMessageId &&
            !contentAppended;
        if (sameWindow)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: true,
                ScrollToMessageId: null,
                ScrollToEnd: false,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: null);
        }

        if (prepended)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: true,
                ScrollToMessageId: null,
                ScrollToEnd: false,
                ShowNewMessageIndicator: appended,
                ObservedThroughMessageId: null);
        }

        if (appended && wasNearBottom)
        {
            return new ClientMessageScrollDecision(
                PreservePrependOffset: false,
                ScrollToMessageId: nextLatestMessageId,
                ScrollToEnd: contentAppended,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: nextLatestMessageId);
        }

        return new ClientMessageScrollDecision(
            PreservePrependOffset: false,
            ScrollToMessageId: null,
            ScrollToEnd: false,
            ShowNewMessageIndicator: appended,
            ObservedThroughMessageId: null);
    }
}
