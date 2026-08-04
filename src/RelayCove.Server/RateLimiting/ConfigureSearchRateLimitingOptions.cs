using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace RelayCove.Server.RateLimiting;

public sealed class ConfigureSearchRateLimitingOptions : IConfigureOptions<RateLimiterOptions>
{
    private const string UnauthenticatedPartition = "search-query:unauthenticated";

    public void Configure(RateLimiterOptions options)
    {
        options.AddPolicy(SearchRateLimitPolicies.Query, context =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                return RateLimitPartition.GetNoLimiter(UnauthenticatedPartition);
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"{SearchRateLimitPolicies.Query}:{subject}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = 30,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    Window = TimeSpan.FromMinutes(1),
                });
        });
    }
}
