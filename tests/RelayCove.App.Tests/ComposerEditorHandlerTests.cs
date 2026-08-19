using RelayCove.App.Platforms.Windows.Handlers;

namespace RelayCove.App.Tests;

public sealed class ComposerEditorHandlerTests
{
    [Fact]
    public void DocumentText_WhenMixedNewlines_RoundTripsAsWindowsText()
    {
        var documentText = ComposerEditorHandler.ToDocumentText("上午\r\n好\n呀");

        Assert.Equal("上午\r好\r呀", documentText);
        Assert.Equal($"上午{Environment.NewLine}好{Environment.NewLine}呀",
            ComposerEditorHandler.FromDocumentText(documentText));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 6)]
    [InlineData(5, 7)]
    public void DocumentIndexToTextIndex_WhenTextContainsParagraphs_MapsCrlfOffsets(
        int documentIndex,
        int expectedTextIndex)
    {
        Assert.Equal(expectedTextIndex,
            ComposerEditorHandler.DocumentIndexToTextIndex("上\r午\r好", documentIndex));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(6, 4)]
    [InlineData(7, 5)]
    public void TextIndexToDocumentIndex_WhenTextContainsCrlf_MapsDocumentOffsets(
        int textIndex,
        int expectedDocumentIndex)
    {
        Assert.Equal(expectedDocumentIndex,
            ComposerEditorHandler.TextIndexToDocumentIndex($"上{Environment.NewLine}午{Environment.NewLine}好", textIndex));
    }

}
