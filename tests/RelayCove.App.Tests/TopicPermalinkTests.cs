using RelayCove.App.Services;

namespace RelayCove.App.Tests;

public sealed class TopicPermalinkTests
{
    [Fact]
    public void Build_WhenTopicHasUnicodeSpacesAndDots_UsesOfficialNarrowHashEncoding()
    {
        var link = TopicPermalink.Build("https://chat.example.test/", 7, "工程 . 频道", "讨论 (第一期)", 42);

        Assert.Equal("https://chat.example.test/#narrow/channel/7-.E5.B7.A5.E7.A8.8B-.2E-.E9.A2.91.E9.81.93/topic/.E8.AE.A8.E8.AE.BA.20.28.E7.AC.AC.E4.B8.80.E6.9C.9F.29/with/42", link);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a.b", "a.2Eb")]
    [InlineData("(x)", ".28x.29")]
    public void EncodeHashComponent_EncodesOnlyOriginalTokens(string value, string expected) =>
        Assert.Equal(expected, TopicPermalink.EncodeHashComponent(value));
}
