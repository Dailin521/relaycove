using System.Text;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class SearchSnippetTests
{
    [Fact]
    public void Create_WhenContentFits_ReturnsContentUnchanged()
    {
        const string content = "前文 MixedCase 中文关键词 后文";

        var snippet = SearchSnippet.Create(content, "mixedcase", contentMatched: true);

        Assert.Equal(content, snippet);
    }

    [Fact]
    public void Create_WhenLongContentMatchesAsciiCaseInsensitively_CentersFirstMatch()
    {
        var content = new string('前', 180) + "FiRsT" + new string('后', 180) + "first";

        var snippet = SearchSnippet.Create(content, "first", contentMatched: true);

        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
        Assert.Contains("FiRsT", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("first", snippet, StringComparison.Ordinal);
        Assert.Equal(SearchSnippet.MaximumScalarLength, CountScalars(snippet));
    }

    [Fact]
    public void Create_WhenNonAsciiCaseDiffers_UsesExactFirstLiteralMatch()
    {
        var content = new string('前', 180) + "Ä" + new string('中', 180) + "ä";

        var snippet = SearchSnippet.Create(content, "ä", contentMatched: true);

        Assert.DoesNotContain("Ä", snippet, StringComparison.Ordinal);
        Assert.Contains("ä", snippet, StringComparison.Ordinal);
        Assert.Equal(SearchSnippet.MaximumScalarLength, CountScalars(snippet));
    }

    [Fact]
    public void Create_WhenMatchAndContextContainEmoji_DoesNotSplitSurrogates()
    {
        var content = string.Concat(
            Enumerable.Repeat("😀", 180)) +
            "猫咪" +
            string.Concat(Enumerable.Repeat("🐈", 180));

        var snippet = SearchSnippet.Create(content, "猫", contentMatched: true);

        Assert.Contains("猫咪", snippet, StringComparison.Ordinal);
        Assert.Equal(SearchSnippet.MaximumScalarLength, CountScalars(snippet));
        Assert.True(IsValidUtf16(snippet));
    }

    [Fact]
    public void Create_WhenOnlyAttachmentMatches_ReturnsBoundedContentStart()
    {
        var content = new string('甲', 200);

        var snippet = SearchSnippet.Create(content, "附件", contentMatched: false);

        Assert.Equal(new string('甲', 159) + "…", snippet);
        Assert.Equal(SearchSnippet.MaximumScalarLength, CountScalars(snippet));
    }

    [Fact]
    public void Create_WhenOnlyAttachmentMatchesAndContentIsNull_ReturnsEmpty()
    {
        Assert.Equal(
            string.Empty,
            SearchSnippet.Create(content: null, "附件", contentMatched: false));
    }

    private static int CountScalars(string value) => value.EnumerateRunes().Count();

    private static bool IsValidUtf16(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
