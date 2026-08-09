using RelayCove.Client.Mentions;

namespace RelayCove.Client.Tests.Mentions;

public sealed class ClientMentionPolicyTests
{
    [Theory]
    [InlineData("@lq", "lq", true)]
    [InlineData("@Alice", "alice", true)]
    [InlineData("hello @Alice!", "ALICE", true)]
    [InlineData("(@alice_1)", "alice_1", true)]
    [InlineData("mail@alice", "alice", false)]
    [InlineData("@@alice", "alice", false)]
    [InlineData("@alice_more", "alice", false)]
    [InlineData("@alice@other", "alice", false)]
    [InlineData("alice", "alice", false)]
    [InlineData("@bob", "alice", false)]
    public void ContainsToken_WhenBoundariesVary_ReturnsExpected(
        string content,
        string userName,
        bool expected) =>
        Assert.Equal(expected, ClientMentionPolicy.ContainsToken(content, userName));

    [Theory]
    [InlineData("lq", true)]
    [InlineData("a", false)]
    public void IsValidUserName_WhenLengthVaries_ReturnsExpected(string userName, bool expected) =>
        Assert.Equal(expected, ClientMentionPolicy.IsValidUserName(userName));

    [Theory]
    [InlineData("", 0, 0, "Alice", "@Alice ", 7)]
    [InlineData("hello", 5, 0, "Alice", "hello @Alice ", 13)]
    [InlineData("hello world", 6, 5, "Alice", "hello @Alice ", 13)]
    [InlineData("say !", 4, 0, "Alice", "say @Alice !", 11)]
    public void TryInsertToken_WhenSelectionIsValid_ReturnsBoundedEdit(
        string content,
        int selectionStart,
        int selectionLength,
        string userName,
        string expectedText,
        int expectedCaret)
    {
        var success = ClientMentionPolicy.TryInsertToken(
            content,
            selectionStart,
            selectionLength,
            userName,
            out var edit);

        Assert.True(success);
        Assert.Equal(expectedText, edit.Text);
        Assert.Equal(expectedCaret, edit.CaretIndex);
        Assert.True(ClientMentionPolicy.ContainsToken(edit.Text, userName));
        Assert.DoesNotContain(userName, edit.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("alice", true)]
    [InlineData("bad query", false)]
    [InlineData("%", false)]
    [InlineData("@", false)]
    public void IsValidQuery_WhenInputVaries_ReturnsExpected(string query, bool expected) =>
        Assert.Equal(expected, ClientMentionPolicy.IsValidQuery(query));

    [Theory]
    [InlineData("@al", 3, true, 0, 3, "al")]
    [InlineData("请 @alice 确认", 8, true, 2, 6, "alice")]
    [InlineData("mail@alice", 10, false, 0, 0, "")]
    [InlineData("@@alice", 7, false, 0, 0, "")]
    [InlineData("@alice!", 6, true, 0, 6, "alice")]
    [InlineData("@alice", 3, true, 0, 6, "al")]
    public void TryGetActiveQuery_WhenCaretIsAtMention_ReturnsReplacementRange(
        string content,
        int caret,
        bool expected,
        int expectedStart,
        int expectedLength,
        string expectedQuery)
    {
        var result = ClientMentionPolicy.TryGetActiveQuery(content, caret, out var query);

        Assert.Equal(expected, result);
        if (!expected)
        {
            Assert.Null(query);
            return;
        }

        Assert.NotNull(query);
        Assert.Equal(expectedStart, query.Start);
        Assert.Equal(expectedLength, query.Length);
        Assert.Equal(expectedQuery, query.Query);
    }

    [Fact]
    public void TryCanonicalizeUserIds_WhenValid_SortsWithoutMutatingInput()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var input = new[] { second, first };

        var success = ClientMentionPolicy.TryCanonicalizeUserIds(input, out var canonical);

        Assert.True(success);
        Assert.Equal([first, second], canonical);
        Assert.Equal([second, first], input);
    }

    [Fact]
    public void TryCanonicalizeUserIds_WhenInvalid_ReturnsEmpty()
    {
        var duplicate = Guid.NewGuid();
        Assert.False(ClientMentionPolicy.TryCanonicalizeUserIds(
            [duplicate, duplicate],
            out var duplicateResult));
        Assert.Empty(duplicateResult);

        Assert.False(ClientMentionPolicy.TryCanonicalizeUserIds(
            Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray(),
            out var overflowResult));
        Assert.Empty(overflowResult);

        Assert.False(ClientMentionPolicy.TryCanonicalizeUserIds(
            [Guid.Empty],
            out var emptyResult));
        Assert.Empty(emptyResult);
    }
}
