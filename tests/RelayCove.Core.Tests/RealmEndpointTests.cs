using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class RealmEndpointTests
{
    [Fact]
    public void Parse_WhenMixedCaseIdnAndDefaultPort_NormalizesOrigin()
    {
        var endpoint = RealmEndpoint.Parse("HTTPS://BÜCHER.example:443/");

        Assert.Equal("https://xn--bcher-kva.example/", endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://example.test/")]
    [InlineData("https://example.test/path")]
    [InlineData("https://user@example.test/")]
    [InlineData("https://example.test/?x=1")]
    [InlineData("https://example.test/#fragment")]
    public void Parse_WhenNotHttpsOrigin_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() => RealmEndpoint.Parse(value));
    }
}
