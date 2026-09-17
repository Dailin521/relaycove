namespace RelayCove.App.Controls;

internal sealed class StickerPagingPolicy
{
    private const double LoadMoreRatio = 0.9d;
    private bool _requested;

    internal void Reset() => _requested = false;

    internal bool TryRequest(double verticalOffset, double scrollableHeight, bool hasMoreItems, bool isLoading)
    {
        if (!ShouldLoadMore(verticalOffset, scrollableHeight, true, false))
        {
            _requested = false;
            return false;
        }
        if (_requested || !hasMoreItems || isLoading) return false;
        _requested = true;
        return true;
    }

    internal static bool ShouldLoadMore(
        double verticalOffset,
        double scrollableHeight,
        bool hasMoreItems,
        bool isLoading) =>
        hasMoreItems && !isLoading && double.IsFinite(verticalOffset) &&
        double.IsFinite(scrollableHeight) && scrollableHeight > 0 &&
        verticalOffset / scrollableHeight > LoadMoreRatio;
}
