using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using RelayCove.Server.Options;

namespace RelayCove.Server.RateLimiting;

public sealed class ConfigureAttachmentRateLimitingOptions(
    IOptions<UploadOptions> uploadOptions) : IConfigureOptions<RateLimiterOptions>
{
    private const string UnauthenticatedPartition = "attachment-upload:unauthenticated";

    public void Configure(RateLimiterOptions options)
    {
        var configuredOptions = uploadOptions.Value;
        options.AddPolicy(AttachmentRateLimitPolicies.Upload, context =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                return RateLimitPartition.GetNoLimiter(UnauthenticatedPartition);
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"{AttachmentRateLimitPolicies.Upload}:{subject}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = configuredOptions.PermitLimit,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    Window = TimeSpan.FromSeconds(configuredOptions.RateLimitWindowSeconds),
                });
        });
    }
}
