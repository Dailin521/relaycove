using RelayCove.Client.Accounts;
using RelayCove.Client.Presentation;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientConversationFilterPolicyTests
{
    [Theory]
    [InlineData(ClientConversationFilter.All, 0, false, true)]
    [InlineData(ClientConversationFilter.All, 1, true, true)]
    [InlineData(ClientConversationFilter.All, 2, false, true)]
    [InlineData(ClientConversationFilter.Unread, 0, true, true)]
    [InlineData(ClientConversationFilter.Unread, 2, false, false)]
    [InlineData(ClientConversationFilter.Channels, 0, false, true)]
    [InlineData(ClientConversationFilter.Channels, 1, true, true)]
    [InlineData(ClientConversationFilter.Channels, 2, true, false)]
    [InlineData(ClientConversationFilter.Direct, 0, true, false)]
    [InlineData(ClientConversationFilter.Direct, 1, true, false)]
    [InlineData(ClientConversationFilter.Direct, 2, false, true)]
    public void Matches_WhenFilterIsApplied_UsesOnlyExistingConversationFacts(
        ClientConversationFilter filter,
        int groupValue,
        bool hasUnread,
        bool expected)
    {
        var item = CreateItem(
            (ClientConversationGroup)groupValue,
            hasUnread,
            "Project Aurora",
            "A normal preview.");

        var actual = ClientConversationFilterPolicy.Matches(item, filter, searchText: null);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("project aurora")]
    [InlineData("PROJECT AURORA")]
    [InlineData("  aurora  ")]
    public void Matches_WhenSearchMatchesNameOrdinalIgnoreCase_ReturnsTrue(string searchText)
    {
        var item = CreateItem(ClientConversationGroup.Public, false, "Project Aurora", "A normal preview.");

        Assert.True(ClientConversationFilterPolicy.Matches(item, ClientConversationFilter.All, searchText));
    }

    [Theory]
    [InlineData("preview text")]
    [InlineData("PREVIEW TEXT")]
    [InlineData("  PrEvIeW TeXt  ")]
    public void Matches_WhenSearchMatchesPreviewOrdinalIgnoreCase_ReturnsTrue(string searchText)
    {
        var item = CreateItem(ClientConversationGroup.Direct, false, "Alice", "Preview text from Alice.");

        Assert.True(ClientConversationFilterPolicy.Matches(item, ClientConversationFilter.Direct, searchText));
    }

    [Fact]
    public void Matches_WhenSearchDoesNotMatchOrPrimaryFilterFails_ReturnsFalse()
    {
        var item = CreateItem(ClientConversationGroup.Direct, false, "Alice", "Preview text from Alice.");

        Assert.False(ClientConversationFilterPolicy.Matches(item, ClientConversationFilter.Direct, "Bob"));
        Assert.False(ClientConversationFilterPolicy.Matches(item, ClientConversationFilter.Unread, "Alice"));
    }

    private static ClientConversationListItemPresentation CreateItem(
        ClientConversationGroup group,
        bool hasUnread,
        string name,
        string preview) =>
        new(
            Guid.NewGuid(),
            group,
            "分组",
            "#",
            "RC",
            name,
            "类型",
            preview,
            "现在",
            hasUnread ? "1" : string.Empty,
            hasUnread,
            string.Empty);
}
