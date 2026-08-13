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
}
