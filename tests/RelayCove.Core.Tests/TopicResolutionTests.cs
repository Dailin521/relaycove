namespace RelayCove.Core.Tests;

public sealed class TopicResolutionTests
{
    [Fact]
    public void Unresolve_WhenTopicHasRepeatedOfficialPrefixes_RemovesThem()
    {
        Assert.True(TopicResolution.IsResolved("✔ done"));
        Assert.Equal("done", TopicResolution.Unresolve("✔ ✔  ✔ done"));
        Assert.Equal("✔ done", TopicResolution.Resolve("done"));
    }
}
