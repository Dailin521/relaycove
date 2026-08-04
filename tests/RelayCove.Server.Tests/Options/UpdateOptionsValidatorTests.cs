using Microsoft.Extensions.Options;
using RelayCove.Server.Options;

namespace RelayCove.Server.Tests.Options;

public sealed class UpdateOptionsValidatorTests
{
    private readonly UpdateOptionsValidator validator = new();

    [Fact]
    public void Validate_WhenManifestPathIsUsable_Succeeds()
    {
        var result = validator.Validate(null, new UpdateOptions { ManifestPath = "updates/manifest.json" });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    public void Validate_WhenManifestPathIsMissingOrInvalid_Fails(string manifestPath)
    {
        var result = validator.Validate(null, new UpdateOptions { ManifestPath = manifestPath });

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Failures!);
    }
}
