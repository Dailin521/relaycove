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
    public void IsHelp_WhenHelpRequested_ReturnsTrue()
    {
        Assert.True(UpdaterArgumentParser.IsHelp(["--help"]));
    }
}
