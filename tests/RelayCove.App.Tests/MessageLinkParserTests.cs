using RelayCove.App.ViewModels;

namespace RelayCove.App.Tests;

public sealed class MessageLinkParserTests
{
    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("HTTP://localhost:8080/a?x=1#part", "http://localhost:8080/a?x=1#part")]
    [InlineData("www.example.com/page", "https://www.example.com/page")]
    [InlineData("https://example.com/中文?q=测试&n=2", "https://example.com/中文?q=测试&n=2")]
    [InlineData("https://[::1]:8080/path", "https://[::1]:8080/path")]
    public void Split_WhenMessageIsWebUrl_CreatesBrowserTargetWithoutChangingText(string source, string target)
    {
        var part = Assert.Single(MessageLinkParser.Split(source));
        Assert.Equal(source, part.Text);
        Assert.Equal(new Uri(target), part.Uri);
    }

    [Theory]
    [InlineData("请看：https://example.com/path。", "https://example.com/path")]
    [InlineData("(https://example.com/page).", "https://example.com/page")]
    [InlineData("见 https://example.com/wiki/A_(B)。", "https://example.com/wiki/A_(B)")]
    [InlineData("[网站](https://example.com/page)", "https://example.com/page")]
    [InlineData("https://example.com/path?q=a%20b&next=%2Fhome#top!", "https://example.com/path?q=a%20b&next=%2Fhome#top")]
    public void Split_WhenUrlHasSurroundingPunctuation_PreservesTextAndExcludesPunctuationFromLink(string source, string expected)
    {
        var parts = MessageLinkParser.Split(source);
        Assert.Equal(source, string.Concat(parts.Select(part => part.Text)));
        var link = Assert.Single(parts, part => part.Uri is not null);
        Assert.Equal(expected, link.Text);
        Assert.Equal(new Uri(expected), link.Uri);
    }

    [Fact]
    public void Split_WhenMessageContainsMultipleLinks_KeepsOrderAndLineBreaks()
    {
        const string source = "第一个 https://example.com/a\r\n第二个 www.example.org，结束";
        var parts = MessageLinkParser.Split(source);
        Assert.Equal(source, string.Concat(parts.Select(part => part.Text)));
        Assert.Equal([new Uri("https://example.com/a"), new Uri("https://www.example.org")],
            parts.Where(part => part.Uri is not null).Select(part => part.Uri));
    }

    [Theory]
    [InlineData("")]
    [InlineData("普通消息，没有链接")]
    [InlineData("https://")]
    [InlineData("http://?invalid")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/example.txt")]
    [InlineData("mailto:person@example.com")]
    [InlineData("ftp://www.example.com")]
    [InlineData("person@www.example.com")]
    public void Split_WhenTextIsNotWebUrl_DoesNotCreateBrowserTarget(string source)
    {
        var parts = MessageLinkParser.Split(source);
        Assert.Equal(source, string.Concat(parts.Select(part => part.Text)));
        Assert.All(parts, part => Assert.Null(part.Uri));
    }
}
