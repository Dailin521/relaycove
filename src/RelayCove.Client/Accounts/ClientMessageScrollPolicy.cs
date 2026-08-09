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
        bool hasNextItems = true,
        bool replacesExistingLocalItem = false)
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
                ScrollToMessageId: null,
                ScrollToEnd: hasNextItems,
                ShowNewMessageIndicator: false,
                ObservedThroughMessageId: nextLatestMessageId);
        }

        var prepended = nextOldestMessageId is { } nextOldest &&
            previousOldestMessageId is { } previousOldest &&
            nextOldest < previousOldest;
        var appended = contentAppended ||
            (!replacesExistingLocalItem &&
            nextLatestMessageId is { } nextLatest &&
            previousLatestMessageId is { } previousLatest &&
            nextLatest > previousLatest);
        var sameWindow = previousOldestMessageId == nextOldestMessageId &&
            previousLatestMessageId == nextLatestMessageId &&
            !contentAppended;
        if (sameWindow)
        {
            return new ClientMessageScrollDecision(
                // A republished window can still grow after layout when an image
                // preview, attachment state, or wrapped text materializes. That is
                // not a history prepend, so compensating by the extent delta makes
                // a manually positioned viewport jump downward.
                PreservePrependOffset: false,
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
                // Keep the continuous conversation viewport anchored to its end.
                // ScrollIntoView targets an individual container and causes a
                // visible reposition when templates later change height.
                ScrollToMessageId: null,
                ScrollToEnd: true,
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
