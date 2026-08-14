namespace RelayCove.App.ViewModels;

internal static class MessageViewportPolicy
{
    internal const int NearTopItemThreshold = 4;
    internal const int NearBottomItemThreshold = 2;
    internal const long AutomaticLoadDebounceMilliseconds = 350;

    internal static bool IsNearBottom(int lastVisibleItemIndex, int itemCount) =>
        itemCount == 0 ||
        lastVisibleItemIndex >= Math.Max(0, itemCount - 1 - NearBottomItemThreshold);

    internal static bool ShouldRequestOlder(
        int firstVisibleItemIndex,
        bool canLoadOlder,
        bool isLoading,
        bool hasError,
        long nowMilliseconds,
        long lastRequestMilliseconds) =>
        firstVisibleItemIndex is >= 0 and <= NearTopItemThreshold &&
        canLoadOlder &&
        !isLoading &&
        !hasError &&
        (lastRequestMilliseconds == long.MinValue ||
         nowMilliseconds - lastRequestMilliseconds >= AutomaticLoadDebounceMilliseconds);
}
