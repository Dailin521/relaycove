using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using RelayCove.Server.Errors;
using RelayCove.Server.Options;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.RateLimiting;

public sealed class ConfigureAuthenticationRateLimitingOptions(
    IOptions<AuthenticationOptions> authenticationOptions) : IConfigureOptions<RateLimiterOptions>
{
    private const string UnknownAddress = "<unknown-address>";

    public void Configure(RateLimiterOptions options)
    {
        var configuredOptions = authenticationOptions.Value;
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            await ApiErrorWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                ApiErrorCodes.RateLimitExceeded,
                "Too many requests.",
                cancellationToken: cancellationToken);
        };

        AddPolicy(options, AuthenticationRateLimitPolicies.Login, configuredOptions.LoginPermitLimit, configuredOptions.RateLimitWindowSeconds);
        AddPolicy(options, AuthenticationRateLimitPolicies.Refresh, configuredOptions.RefreshPermitLimit, configuredOptions.RateLimitWindowSeconds);
    }

    private static void AddPolicy(RateLimiterOptions options, string policyName, int permitLimit, int windowSeconds)
    {
        options.AddPolicy(policyName, context => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{policyName}:{context.Connection.RemoteIpAddress?.ToString() ?? UnknownAddress}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromSeconds(windowSeconds),
            }));
    }
}
