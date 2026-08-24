using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class MessageContentPresentationTests
{
    [Fact]
    public void Parse_WhenSameRealmImageAndFileLinks_ExtractsControlledCards()
    {
        var presentation = MessageContentPresentation.Parse(
            "before\n![shot](/user_uploads/1/shot.png)\n[notes](/user_uploads/1/notes.txt)\nafter",
            RealmEndpoint.Parse("https://chat.example.test/"));

        Assert.Equal("before\n\nafter", presentation.Body.Replace("\r", string.Empty, StringComparison.Ordinal));
        Assert.Collection(
            presentation.Attachments,
            image => Assert.True(image.IsImage),
            file => Assert.True(file.IsFile));
    }

    [Fact]
    public void Parse_WhenCrossRealmOrTemporaryLink_LeavesLiteralRawMarkdown()
    {
        const string content = "[evil](https://evil.example/user_uploads/x.png)\n[temp](/user_uploads/temporary/x.png)";

        var presentation = MessageContentPresentation.Parse(
            content,
            RealmEndpoint.Parse("https://chat.example.test/"));

        Assert.Empty(presentation.Attachments);
        Assert.Equal(content, presentation.Body);
    }

    [Fact]
    public void Parse_WhenMoreThanFourImages_PreservesOverflowLinkInBody()
    {
        var content = string.Join('\n', Enumerable.Range(1, 5).Select(index =>
            $"![image-{index}](/user_uploads/{index}.png)"));

        var presentation = MessageContentPresentation.Parse(
            content,
            RealmEndpoint.Parse("https://chat.example.test/"));

        Assert.Equal(4, presentation.Attachments.Count);
        Assert.Contains("image-5", presentation.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WhenTwoLeadingQuotes_ArrangesBothAsQuoteCards()
    {
        const string content =
            "@_**zhang|9** [said](https://chat.example.test/#narrow/near/559):\n" +
            "```quote\n中午好\n```\n\n" +
            "@_**zhang|9** [said](https://chat.example.test/#narrow/near/562):\n" +
            "```quote\n中午好\n```\n\n好";

        var presentation = MessageContentPresentation.Parse(content, null);

        Assert.Collection(
            presentation.Quotes,
            first => Assert.Equal(("zhang", "中午好"), (first.Sender, first.Body)),
            second => Assert.Equal(("zhang", "中午好"), (second.Sender, second.Body)));
        Assert.Equal("好", presentation.Body);
    }

    [Theory]
    [InlineData("好```", "好")]
    [InlineData("```今天天气还行", "今天天气还行")]
    public void Parse_WhenReplyTouchesClosingFence_RecoversQuoteAndReply(
        string closingLine,
        string expectedReply)
    {
        var content =
            "@_**zhang|9** [said](https://chat.example.test/#narrow/near/559):\n" +
            $"```quote\n天气如何\n{closingLine}";

        var presentation = MessageContentPresentation.Parse(content, null);

        var quote = Assert.Single(presentation.Quotes);
        Assert.Equal("zhang", quote.Sender);
        Assert.Equal("天气如何", quote.Body);
        Assert.Equal(expectedReply, presentation.Body);
    }
}
