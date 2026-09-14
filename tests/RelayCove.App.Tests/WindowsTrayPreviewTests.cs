using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsTrayPreviewTests
{
    [Fact]
    public void ShouldDismissPreview_WhenMouseCrossesGapIntoPreview_KeepsItOpen()
    {
        long? deadline = null;
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(true, false, 0, ref deadline));
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, false, 150, ref deadline));
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, true, 300, ref deadline));
        Assert.Null(deadline);
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, true, 5000, ref deadline));
    }

    [Fact]
    public void ShouldDismissPreview_WhenMouseLeavesBothSurfaces_ClosesAfterGracePeriod()
    {
        long? deadline = null;
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, false, 1000, ref deadline));
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, false, 1449, ref deadline));
        Assert.True(WindowsTrayIconController.ShouldDismissPreview(false, false, 1450, ref deadline));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldDismissPreview_WhenMouseReturns_CancelsPreviousDismissal(bool overIcon, bool overPreview)
    {
        long? deadline = null;
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, false, 0, ref deadline));
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(overIcon, overPreview, 300, ref deadline));
        Assert.Null(deadline);
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, false, 600, ref deadline));
        Assert.False(WindowsTrayIconController.ShouldDismissPreview(false, false, 900, ref deadline));
        Assert.True(WindowsTrayIconController.ShouldDismissPreview(false, false, 1050, ref deadline));
    }

    [Fact]
    public void PreviewCard_WhenConstructed_WiresWholeCardTapToConversationActivation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RelayCove.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName,
            "src", "RelayCove.App", "Platforms", "Windows", "WindowsTrayIconController.cs"));

        Assert.Contains("root.Tapped += OnPreviewTapped;", source);
        var handlerStart = source.IndexOf("private void OnPreviewTapped(", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0);
        var handlerEnd = source.IndexOf('}', handlerStart);
        Assert.Contains("QueueWindowActivation();", source[handlerStart..handlerEnd]);
    }
}
