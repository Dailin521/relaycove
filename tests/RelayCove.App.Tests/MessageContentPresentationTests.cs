using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class MessageContentPresentationTests
{
    [Fact]
    public void ToPlainText_WhenCustomEmojiOverridesUnicode_PreservesImageTokenThroughMarkdownSummary()
    {
        var emojis = new Dictionary<string, RealmEmoji>
        {
            ["1"] = new("1", "rocket", "/user_avatars/1/emoji/images/1.png", false)
        };
        var summary = MessageContentPresentation.ToPlainText("**出发 :rocket:** :+1:", null, emojis);

        Assert.Equal("出发 :rocket: 👍", summary);
        Assert.Single(EmojiShortcodeCatalog.CreateRuns(summary, emojis), run => run.EmojiSourceUrl is not null);
    }

    [Fact]
    public void Parse_WhenShortcodeOccursInsideUrl_PreservesLinkAndRendersSeparateEmoji()
    {
        const string url = "https://example.test/:party:/details";
        var emojis = new Dictionary<string, RealmEmoji>
        {
            ["1"] = new("1", "party", "/user_avatars/1/emoji/images/1.png", false)
        };

        var presentation = MessageContentPresentation.Parse($"{url} :party:", null, emojis);

        Assert.Single(presentation.BodyRuns, run => run.EmojiSourceUrl is not null);
        var link = Assert.Single(presentation.BodyRuns.SelectMany(run => MessageLinkParser.Split(run.Text)),
            part => part.Uri is not null);
        Assert.Equal(url, link.Text);
        Assert.Equal(url + " :party:", presentation.Body);
    }

    [Fact]
    public void Parse_WhenCustomEmojiIsMixedWithText_ProjectsImagesInOrderAndPreservesCode()
    {
        var emojis = new Dictionary<string, RealmEmoji>
        {
            ["1"] = new("1", "rocket", "/user_avatars/1/emoji/images/1.gif", false, "/user_avatars/1/emoji/images/1-still.png")
        };
        const string raw = "hello :rocket: :unknown: :+1:\n`:rocket:`\n```text\n:rocket:\n```";
        var presentation = MessageContentPresentation.Parse(raw, null, emojis);

        var image = Assert.Single(presentation.BodyRuns, run => run.EmojiSourceUrl is not null);
        Assert.Equal(":rocket:", image.Text);
        Assert.Equal("/user_avatars/1/emoji/images/1-still.png", image.EmojiSourceUrl);
        Assert.Equal("hello :rocket: :unknown: 👍\n`:rocket:`\n```text\n:rocket:\n```", presentation.Body);
        Assert.Equal(presentation.Body, string.Concat(presentation.BodyRuns.Select(run => run.Text)));
    }

    [Fact]
    public void MessageItem_WhenEmojiCatalogArrives_ReusesRowAndRefreshesImageProjection()
    {
        const string raw = ":custom_party:";
        var item = new MessageItem("1", 1, 2, "Ada", raw, "10:00");
        Assert.False(item.HasCustomEmoji);
        var notifications = new List<string?>();
        item.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var emojis = new Dictionary<string, RealmEmoji>
        {
            ["1"] = new("1", "custom_party", "/user_avatars/1/emoji/images/1.png", false)
        };
        item.ApplyFrom(new MessageItem("1", 1, 2, "Ada", raw, "10:00", realmEmojis: emojis));

        Assert.True(item.HasCustomEmoji);
        Assert.False(item.HasPlainBody);
        Assert.Equal(raw, item.Content);
        Assert.Equal(raw, item.Body);
        Assert.Contains(nameof(MessageItem.BodyRuns), notifications);
        Assert.Contains(nameof(MessageItem.HasCustomEmoji), notifications);
        item.ApplyFrom(new MessageItem("1", 1, 2, "Ada", raw, "10:00"));
        Assert.False(item.HasCustomEmoji);
        Assert.True(item.HasPlainBody);
    }

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
    [InlineData("![截图](/user_uploads/1/截图.png)", "")]
    [InlineData("[报告.pdf](/user_uploads/1/报告.pdf)", "")]
    [InlineData("[截图](https://chat.example.test/user_uploads/1/shot.PNG)", "")]
    [InlineData("**重点** 和 [说明](https://example.test/page)", "重点 和 说明")]
    [InlineData("*重点* 和 ~~删除线~~", "重点 和 删除线")]
    [InlineData("@_**Bea|8** :smile:", "Bea 😄")]
    [InlineData("\\*原样星号\\* 和 file_name.txt", "*原样星号* 和 file_name.txt")]
    [InlineData("`原始代码`", "原始代码")]
    [InlineData("```csharp\nvar value = 1;\n```", "var value = 1;")]
    [InlineData("```text\n**literal** [link](https://example.test/) :smile:\n```", "**literal** [link](https://example.test/) :smile:")]
    [InlineData("# 标题\n> 引用文本", "标题\n引用文本")]
    [InlineData("[外部说明](https://outside.example.test/user_uploads/1/file.pdf)", "外部说明")]
    [InlineData("[临时文件](/user_uploads/temporary/file.pdf)", "临时文件")]
    [InlineData("**Bea** [said](#):\n```quote\n原始消息\n```\n\n上一条回复", "原始消息\n\n上一条回复")]
    [InlineData("**Bea** [said](#):\n```quote\n![截图](/user_uploads/1/shot.png)\n```", "")]
    public void Parse_WhenQuotedContentContainsMarkdown_ShowsReadableContentAndPreservesRawMessage(
        string quotedContent, string expected)
    {
        var raw = MessageQuote.Build(new MessageItem("message-1", 1, 8, "Bea", quotedContent, "10:00"));
        var message = new MessageItem("message-2", 2, 7, "Ada", raw, "10:01",
            realm: RealmEndpoint.Parse("https://chat.example.test/"));

        Assert.Equal(expected, Assert.Single(message.Quotes).Body);
        Assert.Empty(message.Body);
        Assert.Equal(raw, message.Content);
        Assert.Contains(quotedContent, MessageQuote.Build(message), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WhenQuoteContainsImageAndFile_PreservesPreviewSourcesWithoutChangingRawContent()
    {
        const string quoted = "**看这里**\n![截图](/user_uploads/1/shot.png)\n[报告.pdf](/user_uploads/1/report.pdf)";
        var raw = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", quoted, "10:00")) + "收到";
        var message = new MessageItem("2", 2, 7, "Ada", raw, "10:01",
            realm: RealmEndpoint.Parse("https://chat.example.test/"));

        var quote = Assert.Single(message.Quotes);
        Assert.Equal("看这里", quote.Body);
        Assert.True(quote.HasBody);
        Assert.True(quote.HasAttachments);
        Assert.Collection(quote.Attachments,
            image =>
            {
                Assert.True(image.IsImage);
                Assert.Equal("https://chat.example.test/user_uploads/1/shot.png", image.ImageSourceUrl);
            },
            file =>
            {
                Assert.True(file.IsFile);
                Assert.Equal("报告.pdf", file.Name);
                Assert.Equal("https://chat.example.test/user_uploads/1/report.pdf", file.SourceUrl);
                Assert.Null(file.ImageSourceUrl);
            });
        Assert.Empty(message.Attachments);
        Assert.Equal("收到", message.Body);
        Assert.Equal(raw, message.Content);
        Assert.Contains(quoted, MessageQuote.Build(message), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("![截图](/user_uploads/1/shot.png)")]
    [InlineData("[截图](/user_uploads/1/shot.PNG)")]
    [InlineData("[截图](https://chat.example.test/user_uploads/1/shot.png)")]
    [InlineData("![截图](/user_uploads/1/截图.png)")]
    public void Parse_WhenQuoteContainsOnlyAnImage_ProvidesThumbnailWithoutPlaceholder(string quoted)
    {
        var raw = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", quoted, "10:00"));

        var quote = Assert.Single(MessageContentPresentation.Parse(raw, RealmEndpoint.Parse("https://chat.example.test/")).Quotes);

        Assert.True(Assert.Single(quote.Attachments).IsImage);
        Assert.False(quote.HasBody);
    }

    [Fact]
    public void Parse_WhenQuotesAreNestedAndConsecutive_KeepsAttachmentsWithTheirOwnQuote()
    {
        var nested = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", "![one](/user_uploads/1/one.png)", "10:00"));
        var first = MessageQuote.Build(new MessageItem("2", 2, 7, "Ada", nested + "上一条回复", "10:01"));
        var second = MessageQuote.Build(new MessageItem("3", 3, 9, "Chen", "[two.pdf](/user_uploads/1/two.pdf)", "10:02"));

        var presentation = MessageContentPresentation.Parse(first + second + "![own](/user_uploads/1/own.png)",
            RealmEndpoint.Parse("https://chat.example.test/"));

        Assert.Collection(presentation.Quotes,
            quote =>
            {
                Assert.Equal("上一条回复", quote.Body);
                Assert.Equal("one", Assert.Single(quote.Attachments).Name);
            },
            quote =>
            {
                Assert.False(quote.HasBody);
                Assert.Equal("two.pdf", Assert.Single(quote.Attachments).Name);
            });
        Assert.Equal("own", Assert.Single(presentation.Attachments).Name);
    }

    [Theory]
    [InlineData("https://outside.example.test/user_uploads/1/shot.png", true)]
    [InlineData("/user_uploads/temporary/shot.png", true)]
    [InlineData("http://chat.example.test/user_uploads/1/shot.png", true)]
    [InlineData("file:///C:/shot.png", true)]
    [InlineData("/user_uploads/1/shot.png", false)]
    public void Parse_WhenQuotedImageCannotBeLoadedSafely_UsesItsNameWithoutPreview(string url, bool hasRealm)
    {
        var raw = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", $"![截图]({url})", "10:00"));

        var quote = Assert.Single(MessageContentPresentation.Parse(raw,
            hasRealm ? RealmEndpoint.Parse("https://chat.example.test/") : null).Quotes);

        Assert.Empty(quote.Attachments);
        Assert.Equal("截图", quote.Body);
    }

    [Theory]
    [InlineData("`![literal](/user_uploads/1/shot.png)`")]
    [InlineData("```text\n![literal](/user_uploads/1/shot.png)\n```")]
    public void Parse_WhenQuotedMarkdownIsCode_DoesNotTurnItIntoAnAttachment(string quoted)
    {
        var raw = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", quoted, "10:00"));

        var quote = Assert.Single(MessageContentPresentation.Parse(raw, RealmEndpoint.Parse("https://chat.example.test/")).Quotes);

        Assert.Empty(quote.Attachments);
        Assert.Equal("![literal](/user_uploads/1/shot.png)", quote.Body);
    }

    [Fact]
    public void Parse_WhenQuotedAttachmentsRepeatOrExceedLimits_BoundsPreviewsAndKeepsOverflowNames()
    {
        var images = Enumerable.Range(1, 5).Select(index => $"![image-{index}](/user_uploads/{index}.png)");
        var files = Enumerable.Range(1, 7).Select(index => $"[file-{index}.pdf](/user_uploads/{index}.pdf)");
        var quoted = string.Join('\n', images.Concat(files)) + "\n![repeat](/user_uploads/1.png)";
        var raw = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", quoted, "10:00"));

        var quote = Assert.Single(MessageContentPresentation.Parse(raw, RealmEndpoint.Parse("https://chat.example.test/")).Quotes);

        Assert.Equal(10, quote.Attachments.Count);
        Assert.Equal(4, quote.Attachments.Count(attachment => attachment.IsImage));
        Assert.Contains("image-5", quote.Body, StringComparison.Ordinal);
        Assert.Contains("file-7.pdf", quote.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("repeat", quote.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("[图片]", quote.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyFrom_WhenQuotedImageChangesOrRealmChanges_RefreshesQuoteAttachmentSource()
    {
        var realm = RealmEndpoint.Parse("https://chat.example.test/");
        var firstRaw = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", "![one](https://chat.example.test/user_uploads/one.png)", "10:00"));
        var secondRaw = MessageQuote.Build(new MessageItem("1", 1, 8, "Bea", "![two](https://chat.example.test/user_uploads/two.png)", "10:00"));
        var message = new MessageItem("2", 2, 7, "Ada", firstRaw, "10:01", realm: realm);
        var notifications = new List<string?>();
        message.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        message.ApplyFrom(new MessageItem("2", 2, 7, "Ada", secondRaw, "10:01", realm: realm));

        Assert.Equal("https://chat.example.test/user_uploads/two.png", Assert.Single(Assert.Single(message.Quotes).Attachments).ImageSourceUrl);
        Assert.Contains(nameof(MessageItem.Quotes), notifications);
        notifications.Clear();

        message.ApplyFrom(new MessageItem("2", 2, 7, "Ada", secondRaw, "10:01",
            realm: RealmEndpoint.Parse("https://other.example.test/")));

        var quote = Assert.Single(message.Quotes);
        Assert.Empty(quote.Attachments);
        Assert.Equal("two", quote.Body);
        Assert.Contains(nameof(MessageItem.Quotes), notifications);
    }

    [Fact]
    public void ToPlainText_WhenAttachmentsExceedPreviewLimit_HidesEveryAttachmentLink()
    {
        var content = string.Join('\n', Enumerable.Range(1, 5).Select(index =>
            $"![截图{index}](/user_uploads/{index}.png)"));

        var text = MessageContentPresentation.ToPlainText(content, RealmEndpoint.Parse("https://chat.example.test/"));

        Assert.Equal(string.Join('\n', Enumerable.Repeat("[图片]", 5)), text);
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
