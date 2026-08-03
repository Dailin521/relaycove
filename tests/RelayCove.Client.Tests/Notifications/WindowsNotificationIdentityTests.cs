using RelayCove.Client.Notifications;

namespace RelayCove.Client.Tests.Notifications;

public sealed class WindowsNotificationIdentityTests
{
    private const string AccountScopeId =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void GetConversationGroup_WhenInputsAreCanonical_MatchesFrozenSpecificationVector()
    {
        var group = WindowsNotificationIdentity.GetConversationGroup(
            AccountScopeId,
            ConversationId);

        Assert.Equal("xzXuM4KwnuCXvzCWGT65Ptnpk0W6_yV678dFK5IRi20", group);
    }

    [Fact]
    public void GetSummaryGroup_WhenScopeIsCanonical_MatchesFrozenSpecificationVector()
    {
        var group = WindowsNotificationIdentity.GetSummaryGroup(AccountScopeId);

        Assert.Equal("ixtbwSB8U_2_R3Yb4lTASV38xQVX5opLhGkUGXOymEY", group);
    }

    [Fact]
    public void Groups_WhenScopeOrConversationChanges_AreIsolated()
    {
        var original = WindowsNotificationIdentity.GetConversationGroup(
            AccountScopeId,
            ConversationId);

        Assert.NotEqual(
            original,
            WindowsNotificationIdentity.GetConversationGroup(
                new string('B', 42) + "A",
                ConversationId));
        Assert.NotEqual(
            original,
            WindowsNotificationIdentity.GetConversationGroup(
                AccountScopeId,
                Guid.Parse("21111111-2222-3333-4444-555555555555")));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ValidateAccountScopeId_WhenEncodingIsNotCanonical_Rejects(string accountScopeId)
    {
        Assert.False(WindowsNotificationIdentity.IsValidAccountScopeId(accountScopeId));
        Assert.Throws<ArgumentException>(() =>
            WindowsNotificationIdentity.ValidateAccountScopeId(accountScopeId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetMessageTag_WhenMessageIdIsNotPositive_Rejects(long messageId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsNotificationIdentity.GetMessageTag(messageId));
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(9223372036854775807, "9223372036854775807")]
    public void GetMessageTag_WhenMessageIdIsPositive_UsesInvariantDecimal(
        long messageId,
        string expected)
    {
        Assert.Equal(expected, WindowsNotificationIdentity.GetMessageTag(messageId));
    }
}
