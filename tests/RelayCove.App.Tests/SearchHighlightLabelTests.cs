using RelayCove.App.Controls;

namespace RelayCove.App.Tests;

public sealed class SearchHighlightLabelTests
{
    [Fact]
    public void Split_WhenQueryOccursMoreThanOnce_HighlightsEveryCaseInsensitiveMatch()
    {
        var parts = SearchHighlightLabel.Split("g1Gfa1", "1");

        Assert.Equal(
            [("g", false), ("1", true), ("Gfa", false), ("1", true)],
            parts);
    }

    [Fact]
    public void Split_WhenQueryIsEmpty_ReturnsPlainText()
    {
        Assert.Equal([("plain", false)], SearchHighlightLabel.Split("plain", "  "));
    }
}
