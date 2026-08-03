using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace RelayCove.Server.Tests.Options;

public sealed class AuthenticationStartupTests
{
    [Fact]
    public void Startup_WhenSigningKeyIsMissing_FailsBeforeServingRequests()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:SigningKey"] = string.Empty,
                }));
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Authentication:SigningKey", exception.ToString(), StringComparison.Ordinal);
    }
}
