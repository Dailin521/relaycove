using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Data;

public sealed class MessageTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_WhenTextIsValid_PreservesExactContentAndNormalizesTime()
    {
        const string content = "  exact 🛰️ text\t\r\n";

        var message = CreateMessage(content, CreatedAt.AddTicks(9999));

        Assert.Equal(content, message.Content);
        Assert.Equal(CreatedAt, message.CreatedAt);
        Assert.Equal(MessageType.Text, message.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t\r\n")]
    [InlineData("has\0control")]
    public void Constructor_WhenTextIsInvalid_Throws(string content)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateMessage(content, CreatedAt));
    }

    [Fact]
    public void Constructor_WhenTextContainsMalformedUnicode_Throws()
    {
        const string malformed = "\uD800";

        Assert.Throws<ArgumentException>(() => CreateMessage(malformed, CreatedAt));
    }

    [Fact]
    public void Constructor_WhenTextExceedsUnicodeScalarLimit_Throws()
    {
        var atLimit = string.Concat(Enumerable.Repeat("🛰", Message.MaximumContentLength));
        var overLimit = atLimit + "x";

        Assert.Equal(atLimit, CreateMessage(atLimit, CreatedAt).Content);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateMessage(overLimit, CreatedAt));
    }

    [Fact]
    public void Mentions_WhenInvalidDuplicateOrOverLimit_ThrowWithoutPartialMutation()
    {
        var message = CreateMessage("hello", CreatedAt);
        var first = Guid.NewGuid();
        message.AddMention(first);

        Assert.Throws<ArgumentException>(() => message.AddMention(Guid.Empty));
        Assert.Throws<ArgumentException>(() => message.AddMention(first));
        for (var index = 1; index < Message.MaximumMentionCount; index++)
        {
            message.AddMention(Guid.NewGuid());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => message.AddMention(Guid.NewGuid()));
        Assert.Equal(Message.MaximumMentionCount, message.Mentions.Count);
    }

    [Fact]
    public void Constructor_WhenIdentityTypeReplyOrTimeIsInvalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Message(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), MessageType.Text, "text", null, CreatedAt));
        Assert.Throws<ArgumentException>(() => new Message(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), MessageType.Text, "text", null, CreatedAt));
        Assert.Throws<ArgumentException>(() => new Message(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, MessageType.Text, "text", null, CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Message(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), (MessageType)99, "text", null, CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Message(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), MessageType.Text, "text", 0, CreatedAt));
        Assert.Throws<ArgumentException>(() => new Message(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), MessageType.Text, "text", null,
            DateTime.SpecifyKind(CreatedAt, DateTimeKind.Local)));
    }

    private static Message CreateMessage(string content, DateTime createdAt) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        MessageType.Text,
        content,
        null,
        createdAt);
}
