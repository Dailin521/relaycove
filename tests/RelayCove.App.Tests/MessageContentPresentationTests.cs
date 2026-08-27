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

    [Fact]
    public void Parse_WhenKnownEmojiShortcodesExist_ProjectsUnicodeWithoutChangingCodeSpans()
    {
        const string content =
            ":melting_face: :+1: :unknown_relaycove_emoji:\n" +
            "`:melting_face:`\n" +
            "```text\n:melting_face:\n```";

        var presentation = MessageContentPresentation.Parse(content, null);

        Assert.Equal(
            "🫠 👍 :unknown_relaycove_emoji:\n`:melting_face:`\n```text\n:melting_face:\n```",
            presentation.Body.Replace("\r", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void MessageItem_WhenEmojiShortcodeIsProjected_KeepsRawContentAuthority()
    {
        const string raw = ":melting_face:";

        var message = new MessageItem("message-1", 1, 2, "Ada", raw, "10:00");

        Assert.Equal(raw, message.Content);
        Assert.Equal("🫠", message.Body);
        Assert.Contains(raw, message.AccessibleLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageItem_WhenContentIsOnlyImages_UsesImageOnlyPresentation()
    {
        var realm = RealmEndpoint.Parse("https://chat.example.test/");
        var imageOnly = new MessageItem(
            "message-1",
            1,
            2,
            "Ada",
            "![shot](/user_uploads/1/shot.png)",
            "10:00",
            realm: realm);
        var imageWithText = new MessageItem(
            "message-2",
            2,
            2,
            "Ada",
            "caption\n![shot](/user_uploads/1/shot.png)",
            "10:01",
            realm: realm);
        var imageWithFile = new MessageItem(
            "message-3",
            3,
            2,
            "Ada",
            "![shot](/user_uploads/1/shot.png)\n[notes](/user_uploads/1/notes.txt)",
            "10:02",
            realm: realm);

        Assert.True(imageOnly.IsImageOnly);
        Assert.False(imageWithText.IsImageOnly);
        Assert.False(imageWithFile.IsImageOnly);
    }

    [Fact]
    public void SearchContentClassifier_WhenMessageContainsMixedContent_ProjectsEveryCategory()
    {
        const string content =
            "[notes](/user_uploads/1/notes.pdf)\n" +
            "![shot](/user_uploads/1/shot.png)\n" +
            "[clip](/user_uploads/1/clip.mp4)\n" +
            "[site](https://example.test/page)";

        var kinds = SearchContentClassifier.Classify(
            content,
            RealmEndpoint.Parse("https://chat.example.test/"));

        Assert.True(kinds.HasFlag(SearchContentKind.Message));
        Assert.True(kinds.HasFlag(SearchContentKind.File));
        Assert.True(kinds.HasFlag(SearchContentKind.Image));
        Assert.True(kinds.HasFlag(SearchContentKind.Video));
        Assert.True(kinds.HasFlag(SearchContentKind.Link));
    }
}
