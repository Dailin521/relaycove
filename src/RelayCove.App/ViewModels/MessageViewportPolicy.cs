namespace RelayCove.App.ViewModels;

internal static class MessageViewportPolicy
{
    internal const int NearTopItemThreshold = 4;
    internal const int NearBottomItemThreshold = 2;
    internal const double NearBottomDistanceDip = 96d;
    internal const long AutomaticLoadDebounceMilliseconds = 350;

    internal static bool IsNearBottom(double? bottomDistanceDip, int lastVisibleItemIndex, int itemCount)
    {
        if (bottomDistanceDip is { } distance && double.IsFinite(distance))
        {
            return Math.Max(0d, distance) <= NearBottomDistanceDip;
        }

        return itemCount == 0 ||
            lastVisibleItemIndex >= Math.Max(0, itemCount - 1 - NearBottomItemThreshold);
    }

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
