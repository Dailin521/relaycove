using System.IdentityModel.Tokens.Jwt;
using RelayCove.Server.Errors;
using RelayCove.Server.Services;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/users", ListAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        UserDirectoryQueryService queryService,
        CancellationToken cancellationToken)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var actorUserId))
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.AuthenticationRequired,
                "Authentication is required.");
        }

        var result = await queryService.ListAsync(actorUserId, cancellationToken);
        return result.Status == ConversationOperationStatus.Success
            ? Results.Ok(result.Value)
            : ApiErrorWriter.Result(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.AccessDenied,
                "Access is denied.");
    }
}
