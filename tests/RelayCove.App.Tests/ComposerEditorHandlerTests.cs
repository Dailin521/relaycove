using RelayCove.App.Platforms.Windows.Handlers;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class ComposerEditorHandlerTests
{
    [Fact]
    public void ComposerDocumentText_WhenImagesAndNewlinesExist_ReconstructsRawDraftAndSelectionOffsets()
    {
        var document = new ComposerDocumentText("你\uFFFC好\r\uFFFC🚀", new Dictionary<int, string>
        {
            [1] = ":party:", [4] = ":rocket:"
        });

        Assert.Equal("你:party:好\r\n:rocket:🚀", document.RawText);
        Assert.Equal(1, document.ToTextIndex(1));
        Assert.Equal(8, document.ToTextIndex(2));
        Assert.Equal(11, document.ToTextIndex(4));
        Assert.Equal(19, document.ToTextIndex(5));
        foreach (var index in new[] { 0, 1, 2, 3, 4, 5, 7 })
            Assert.Equal(index, document.ToDocumentIndex(document.ToTextIndex(index)));
        Assert.Equal(1, document.ToDocumentIndex(4));
        Assert.Equal(7, document.ToDocumentIndex(int.MaxValue));
    }

    [Fact]
    public void CreateRuns_WhenUsedForComposer_PreservesEveryRawCharacterIncludingUnicodeShortcodesAndCode()
    {
        const string raw = "你:party: :rocket: :+1: `:party:` https://example.test/:party:/x";
        var emojis = new Dictionary<string, RealmEmoji>
        {
            ["1"] = new("1", "party", "/user_avatars/1/emoji/images/1.png", false)
        };

        var runs = EmojiShortcodeCatalog.CreateRuns(raw, emojis, replaceUnicode: false);

        Assert.Equal(raw, string.Concat(runs.Select(run => run.Text)));
        Assert.Equal(":party:", Assert.Single(runs, run => run.EmojiSourceUrl is not null).Text);
    }

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

    [Fact]
    public void GetDocumentSelection_WhenQuotedDraftUsesLf_PlacesCaretAfterClosingFence()
    {
        const string draft = "header\n```quote\nbody\n```\n\n";

        var selection = ComposerEditorHandler.GetDocumentSelection(draft, draft.Length, 0);

        Assert.Equal(ComposerEditorHandler.ToDocumentText(draft).Length, selection.Start);
        Assert.Equal(selection.Start, selection.End);
    }

}
