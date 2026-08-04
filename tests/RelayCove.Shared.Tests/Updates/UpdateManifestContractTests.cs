using System.Text.Json;
using RelayCove.Shared.Updates;

namespace RelayCove.Shared.Tests.Updates;

public sealed class UpdateManifestContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void UpdateManifest_WhenSerialized_UsesStableCamelCaseContract()
    {
        var manifest = CreateManifest();

        var json = JsonSerializer.Serialize(manifest, WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["schemaVersion", "channel", "version", "minimumSupportedVersion", "mandatory", "artifact", "releaseNotes"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["type", "url", "sizeBytes", "sha256"],
            document.RootElement.GetProperty("artifact").EnumerateObject().Select(property => property.Name));
        Assert.Equal(manifest, JsonSerializer.Deserialize<UpdateManifestDto>(json, WebJson));
    }

    [Theory]
    [InlineData("0.0.0", true)]
    [InlineData("1.2.3-rc.6", true)]
    [InlineData("1.2.3-alpha.1+build.5", false)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2.3-01", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3-", false)]
    [InlineData("1.2.3-rc_1", false)]
    public void TryParse_WhenGivenStrictSubset_ReturnsExpectedResult(string input, bool expected)
    {
        var result = SemanticVersion.TryParse(input, out var version);

        Assert.Equal(expected, result);
        Assert.Equal(expected ? input : null, version?.ToString());
    }

    [Fact]
    public void CompareTo_WhenGivenSemVerPrecedenceOrder_UsesNumericAndPrereleaseRules()
    {
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
            "1.0.1",
            "1.0.4294967296",
        };

        for (var index = 0; index < ordered.Length - 1; index++)
        {
            Assert.True(SemanticVersion.Parse(ordered[index]).CompareTo(SemanticVersion.Parse(ordered[index + 1])) < 0);
        }
    }

    [Theory]
    [InlineData("http://updates.example.test/release.zip")]
    [InlineData("https://user@updates.example.test/release.zip")]
    [InlineData("https://updates.example.test/release.zip#part")]
    public void TryValidate_WhenArtifactUrlIsUnsafe_RejectsManifest(string url)
    {
        var manifest = CreateManifest() with { Artifact = CreateManifest().Artifact with { Url = url } };

        var valid = UpdateManifestValidator.TryValidate(manifest, out var error);

        Assert.False(valid);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryValidate_WhenArtifactUrlExceedsLimit_RejectsManifest()
    {
        var oversizedUrl = "https://updates.example.test/" +
            new string('a', UpdateConstants.MaximumArtifactUrlLength) + ".zip";
        var manifest = CreateManifest() with { Artifact = CreateManifest().Artifact with { Url = oversizedUrl } };

        var valid = UpdateManifestValidator.TryValidate(manifest, out var error);

        Assert.False(valid);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", false, UpdateDecisionKind.None)]
    [InlineData("1.0.0", "1.0.1", false, UpdateDecisionKind.Optional)]
    [InlineData("1.0.0", "1.0.1", true, UpdateDecisionKind.Mandatory)]
    [InlineData("0.9.9", "1.0.1", false, UpdateDecisionKind.Unsupported)]
    public void Evaluate_WhenManifestIsValid_ReturnsFailClosedDecision(
        string currentVersion,
        string targetVersion,
        bool mandatory,
        UpdateDecisionKind expected)
    {
        var manifest = CreateManifest() with { Version = targetVersion, Mandatory = mandatory };

        var decision = UpdateDecisionEvaluator.Evaluate(currentVersion, manifest);

        Assert.Equal(expected, decision);
    }

    [Fact]
    public void Evaluate_WhenManifestIsInvalid_ThrowsInsteadOfOfferingAnUpdate()
    {
        var invalidManifest = CreateManifest() with { Artifact = CreateManifest().Artifact with { Sha256 = "ABC" } };

        Assert.Throws<ArgumentException>(() => UpdateDecisionEvaluator.Evaluate("1.0.0", invalidManifest));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("not-a-version", "1.0.0")]
    public void TryValidate_WhenCoreConstraintsAreBroken_RejectsManifest(string version, string minimumSupportedVersion)
    {
        var manifest = CreateManifest() with
        {
            Version = version,
            MinimumSupportedVersion = minimumSupportedVersion,
        };

        var valid = UpdateManifestValidator.TryValidate(manifest, out _);

        Assert.False(valid);
    }

    private static UpdateManifestDto CreateManifest()
    {
        return new UpdateManifestDto(
            UpdateConstants.SchemaVersion,
            UpdateConstants.Channel,
            "1.0.1-rc.1",
            "1.0.0",
            false,
            new UpdateArtifactDto(
                UpdateConstants.ArtifactTypePortableZip,
                "https://updates.example.test/RelayCove.Client-1.0.1-rc.1-win-x64.zip",
                42,
                new string('a', 64)),
            "Internal RC update.");
    }
}
