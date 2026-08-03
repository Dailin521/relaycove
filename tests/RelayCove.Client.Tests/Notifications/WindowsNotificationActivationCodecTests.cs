using RelayCove.Client.Notifications;

namespace RelayCove.Client.Tests.Notifications;

public sealed class WindowsNotificationActivationCodecTests
{
    private const string AccountScopeId =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void MessageTarget_WhenEncoded_RoundTripsCanonicalDiscriminatedFields()
    {
        var argument = WindowsNotificationActivationCodec.EncodeToArgument(
            ClientNotificationActivationTarget.Message(
                AccountScopeId,
                ConversationId,
                42));

        Assert.Equal(
            "v=1;target=message;account=" + AccountScopeId +
            ";conversation=11111111-2222-3333-4444-555555555555;message=42",
            argument);
        Assert.True(WindowsNotificationActivationCodec.TryDecode(argument, out var decoded));
        Assert.Equal(ClientNotificationActivationKind.Message, decoded!.Kind);
        Assert.Equal(AccountScopeId, decoded.AccountScopeId);
        Assert.Equal(ConversationId, decoded.ConversationId);
        Assert.Equal(42, decoded.MessageId);
    }

    [Fact]
    public void UnreadOverviewTarget_WhenEncoded_DoesNotInventMessageIdentity()
    {
        var argument = WindowsNotificationActivationCodec.EncodeToArgument(
            ClientNotificationActivationTarget.UnreadOverview(AccountScopeId));

        Assert.Equal("v=1;target=unread;account=" + AccountScopeId, argument);
        Assert.True(WindowsNotificationActivationCodec.TryDecode(argument, out var decoded));
        Assert.Equal(ClientNotificationActivationKind.UnreadOverview, decoded!.Kind);
        Assert.Null(decoded.ConversationId);
        Assert.Null(decoded.MessageId);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void TryDecode_WhenArgumentIsAmbiguousOrMalformed_FailsClosed(string? argument)
    {
        Assert.False(WindowsNotificationActivationCodec.TryDecode(argument, out var target));
        Assert.Null(target);
    }

    [Fact]
    public void Targets_WhenFormatted_RedactEveryIdentity()
    {
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            42);

        var formatted = target.ToString();

        Assert.DoesNotContain(AccountScopeId, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(ConversationId.ToString(), formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("42", formatted, StringComparison.Ordinal);
    }

    public static TheoryData<string?> InvalidArguments() =>
        new()
        {
            null,
            "",
            "v=1&target=unread&account=" + AccountScopeId,
            "v=2;target=unread;account=" + AccountScopeId,
            "v=1;v=1;target=unread;account=" + AccountScopeId,
            "v=1;target=unread;account=" + AccountScopeId + ";message=1",
            "v=1;target=message;account=" + AccountScopeId +
                ";conversation=11111111-2222-3333-4444-555555555555;message=0",
            "v=1;target=message;account=" + AccountScopeId +
                ";conversation=11111111-2222-3333-4444-555555555555;message=01",
            "v=1;target=message;account=" + AccountScopeId +
                ";conversation=11111111-2222-3333-4444-55555555555A;message=1",
            "v=1;target=unread;account=%41" + new string('A', 42),
            "v=1;target=unread;account=" + new string('A', 42),
            "v=1;target=unread;account=" + new string('A', 42) + "+",
            "v=1;target=unread;account=" + new string('A', 42) + "B",
            "v=1;target=unread;account=" + new string('A', 42) + "%",
            new string('a', 2049),
        };
}
