using RelayCove.Client.Accounts;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientMessageLinkParserTests
{
    [Fact]
    public void Parse_WhenContentContainsLinks_RecognizesHttpAndHttpsAndDeduplicates()
    {
        var links = ClientMessageLinkParser.Parse(
            "中文https://Example.com/a?q=1。 HTTP://other.test/path, " +
            "https://example.com/a?q=1");

        Assert.Equal(2, links.Count);
        Assert.Equal("https://Example.com/a?q=1", links[0].DisplayText);
        Assert.Equal("https://example.com/a?q=1", links[0].AbsoluteUri);
        Assert.Equal("HTTP://other.test/path", links[1].DisplayText);
        Assert.Equal("http://other.test/path", links[1].AbsoluteUri);
    }

    [Fact]
    public void Parse_WhenBalancedAndUnmatchedParenthesesExist_TrimsOnlyOuterPunctuation()
    {
        var link = Assert.Single(ClientMessageLinkParser.Parse(
            "查看 (https://example.test/a_(b))."));

        Assert.Equal("https://example.test/a_(b)", link.DisplayText);
        Assert.Equal("https://example.test/a_(b)", link.AbsoluteUri);
    }

    [Fact]
    public void Parse_WhenCandidateIsDeceptiveOrUnsupported_RejectsIt()
    {
        var links = ClientMessageLinkParser.Parse(
            "nothttps://bad.test javascript:https://bad.test ftp://bad.test " +
            "https://user:pass@credential.test https://backslash.test\\@evil.test " +
            "https://format.test/\u202Epath https://good.test/path");

        var link = Assert.Single(links);
        Assert.Equal("https://good.test/path", link.DisplayText);
        Assert.Equal("https://good.test/path", link.AbsoluteUri);
    }

    [Fact]
    public void Parse_WhenLimitsAreExceeded_SkipsLongCandidateAndReturnsFirstEightDistinct()
    {
        var tooLong = "https://long.test/" +
            new string('a', ClientMessageLinkParser.MaxLinkLength);
        var candidates = string.Join(
            ' ',
            Enumerable.Range(1, 10).Select(index => $"https://example{index}.test/path"));

        var links = ClientMessageLinkParser.Parse($"{tooLong} {candidates}");

        Assert.Equal(ClientMessageLinkParser.MaxLinksPerMessage, links.Count);
        Assert.Equal("https://example1.test/path", links[0].AbsoluteUri);
        Assert.Equal("https://example8.test/path", links[^1].AbsoluteUri);
    }

    [Fact]
    public void Parse_WhenNoSafeLinkExists_ReturnsEmptyReadOnlyView()
    {
        var links = ClientMessageLinkParser.Parse(
            "mailto:user@example.test file:///C:/secret javascript:alert(1) https:///missing");

        Assert.Empty(links);
        Assert.IsAssignableFrom<IReadOnlyList<ClientMessageLinkPresentation>>(links);
    }

    [Fact]
    public void ToString_WhenLinkExists_RedactsDisplayAndAbsoluteUri()
    {
        var link = Assert.Single(ClientMessageLinkParser.Parse(
            "https://classified.example/secret?q=token"));

        Assert.DoesNotContain("classified", link.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", link.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", link.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
