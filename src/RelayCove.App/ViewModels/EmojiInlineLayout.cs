namespace RelayCove.App.ViewModels;

internal static class EmojiInlineLayout
{
    internal const int ComposerSize = 19;

    // Reserve the same descent as the surrounding font so an image straddles
    // the baseline instead of sitting entirely above it.
    internal static int Descent(double fontSize) => Math.Max(1, (int)Math.Round(fontSize * 0.2));
}
