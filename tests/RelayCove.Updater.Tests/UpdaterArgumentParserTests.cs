namespace RelayCove.Updater.Tests;

public sealed class UpdaterArgumentParserTests
{
    [Fact]
    public void TryParse_WhenArgumentsAreValid_ParsesStructuredApplyRequest()
    {
        var valid = UpdaterArgumentParser.TryParse(TestArguments.Create(), out var options);

        Assert.True(valid);
        Assert.NotNull(options);
        Assert.Equal("1.0.1-rc.1", options.ExpectedVersion.ToString());
        Assert.Equal(60, options.WaitTimeoutSeconds);
        Assert.Equal("1234567890abcdef1234567890abcdef", options.BootstrapToken);
    }

    [Theory]
    [InlineData("1.0.0-rc.1")]
    [InlineData("1.0.1+build")]
    public void TryParse_WhenVersionDoesNotAdvanceOrIsInvalid_Rejects(string expectedVersion)
    {
        var arguments = TestArguments.Create("--expected-version", expectedVersion);

        Assert.False(UpdaterArgumentParser.TryParse(arguments, out _));
    }

    [Fact]
    public void TryParse_WhenHashIsNotLowercase_Rejects()
    {
        var arguments = TestArguments.Create("--expected-sha256", new string('A', 64));

        Assert.False(UpdaterArgumentParser.TryParse(arguments, out _));
    }

    [Fact]
    public void TryParse_WhenArtifactExceedsMaximum_Rejects()
    {
        var arguments = TestArguments.Create(
            "--expected-size",
            (RelayCove.Shared.Updates.UpdateConstants.MaximumArtifactBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.False(UpdaterArgumentParser.TryParse(arguments, out _));
    }

    [Theory]
    [InlineData("1234567890ABCDEF1234567890ABCDEF")]
    [InlineData("not-a-token")]
    [InlineData("00000000000000000000000000000000")]
    public void TryParse_WhenBootstrapTokenIsInvalid_Rejects(string bootstrapToken)
    {
        var arguments = TestArguments.Create("--bootstrap-token", bootstrapToken);

        Assert.False(UpdaterArgumentParser.TryParse(arguments, out _));
    }

    [Fact]
    public void CreateLayout_WhenTargetIsUnc_Rejects()
    {
        Assert.True(UpdaterArgumentParser.TryParse(TestArguments.Create("--target", @"\\server\share\RelayCove"), out var options));

        Assert.Throws<InvalidDataException>(() => UpdateLayout.Create(options!, Path.Combine(Path.GetTempPath(), "RelayCove.Updater.exe")));
    }

    [Fact]
    public void IsHelp_WhenHelpRequested_ReturnsTrue()
    {
        Assert.True(UpdaterArgumentParser.IsHelp(["--help"]));
    }
}
