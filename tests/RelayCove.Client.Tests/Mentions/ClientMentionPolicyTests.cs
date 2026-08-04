using RelayCove.Client.Mentions;

namespace RelayCove.Client.Tests.Mentions;

public sealed class ClientMentionPolicyTests
{
    [Theory]
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
    [InlineData("bad query")]
    [InlineData("%")]
    [InlineData("@")]
    [InlineData("")]
    public void IsValidQuery_WhenUnsupported_ReturnsFalse(string query) =>
        Assert.False(ClientMentionPolicy.IsValidQuery(query));

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
