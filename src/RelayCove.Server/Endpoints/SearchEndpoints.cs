using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RelayCove.Server.Errors;
using RelayCove.Server.RateLimiting;
using RelayCove.Server.Services;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/search", SearchAsync)
            .RequireAuthorization()
            .RequireRateLimiting(SearchRateLimitPolicies.Query);
        return endpoints;
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery] string? keyword,
        [FromQuery] Guid? conversationId,
        [FromQuery] int? limit,
        HttpContext context,
        SearchQueryValidator validator,
        SearchQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.Validate(keyword, limit);
        if (errors.Count > 0)
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid.",
                errors);
        }

        var result = await queryService.SearchAsync(
            actorUserId,
            SearchQueryValidator.NormalizeKeyword(keyword!),
            conversationId,
            limit ?? SearchQueryValidator.DefaultLimit,
            cancellationToken);
        return result.Status switch
        {
            SearchOperationStatus.Success => Results.Ok(result.Value),
            SearchOperationStatus.InvalidRequest => ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid."),
            SearchOperationStatus.AccessRevoked => ApiErrorWriter.Result(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.ConversationAccessRevoked,
                "Conversation access is unavailable."),
            _ => throw new InvalidOperationException("Unknown search operation result."),
        };
    }

    private static IResult AuthenticationRequired(HttpContext context) =>
        ApiErrorWriter.Result(
            context,
            StatusCodes.Status401Unauthorized,
            ApiErrorCodes.AuthenticationRequired,
            "Authentication is required.");

    private static bool TryGetActorUserId(HttpContext context, out Guid actorUserId)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParseExact(subject, "D", out actorUserId);
    }
}
