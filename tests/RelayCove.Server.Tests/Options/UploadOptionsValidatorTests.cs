using RelayCove.Server.Options;
using RelayCove.Server.Tests.Infrastructure;

namespace RelayCove.Server.Tests.Options;

public sealed class UploadOptionsValidatorTests
{
    private readonly UploadOptionsValidator uploadValidator = new();
    private readonly StorageOptionsValidator storageValidator = new();

    [Fact]
    public void Validate_WhenUploadOptionsAreValid_Succeeds()
    {
        var result = uploadValidator.Validate(null, new UploadOptions
        {
            MaximumFileBytes = UploadOptions.DefaultMaximumFileBytes,
            PermitLimit = 10,
            RateLimitWindowSeconds = 60,
            UnboundRetentionHours = UploadOptions.DefaultUnboundRetentionHours,
        });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public void Validate_WhenUnboundRetentionIsOutsideBound_Fails(int retentionHours)
    {
        var result = uploadValidator.Validate(null, new UploadOptions
        {
            UnboundRetentionHours = retentionHours,
        });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0, 10, 60)]
    [InlineData(1048575, 10, 60)]
    [InlineData(104857601, 10, 60)]
    [InlineData(1048576, 0, 60)]
    [InlineData(1048576, 1001, 60)]
    [InlineData(1048576, 10, 0)]
    [InlineData(1048576, 10, 86401)]
    public void Validate_WhenUploadOptionIsOutsideBound_Fails(
        long maximumFileBytes,
        int permitLimit,
        int windowSeconds)
    {
        var result = uploadValidator.Validate(null, new UploadOptions
        {
            MaximumFileBytes = maximumFileBytes,
            PermitLimit = permitLimit,
            RateLimitWindowSeconds = windowSeconds,
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WhenStoragePathIsMissingOrInvalid_Fails()
    {
        Assert.True(storageValidator.Validate(null, new StorageOptions { UploadsPath = " " }).Failed);
        Assert.True(storageValidator.Validate(null, new StorageOptions { UploadsPath = "bad\0path" }).Failed);
        Assert.True(storageValidator.Validate(null, new StorageOptions { UploadsPath = "data/uploads" }).Succeeded);
    }

    [Fact]
    public void Startup_WhenUploadOptionsAreInvalid_FailsValidationOnStart()
    {
        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Uploads:MaximumFileBytes"] = "0",
            });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Uploads:MaximumFileBytes", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_WhenUploadMaximumIsBelowOneMib_FailsValidationOnStart()
    {
        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Uploads:MaximumFileBytes"] = (UploadOptions.MinimumMaximumFileBytes - 1).ToString(),
            });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Uploads:MaximumFileBytes", exception.ToString(), StringComparison.Ordinal);
    }
}
