using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using RelayCove.Server.Errors;
using RelayCove.Server.Services;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/sync", GetPageAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> GetPageAsync(
        [FromQuery] long? cursor,
        [FromQuery] long? snapshotUpperBound,
        [FromQuery] int? limit,
        HttpContext context,
        SyncRequestValidator validator,
        MessageSyncService syncService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.Validate(cursor, snapshotUpperBound, limit);
        if (errors.Count > 0)
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid.",
                errors);
        }

        var result = await syncService.GetPageAsync(
            actorUserId,
            cursor!.Value,
            snapshotUpperBound,
            limit ?? SyncRequestValidator.DefaultLimit,
            cancellationToken);
        return result.Status switch
        {
            SyncOperationStatus.Success => Results.Ok(result.Value),
            SyncOperationStatus.InvalidRequest => ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid."),
            SyncOperationStatus.CursorInvalid => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.SyncCursorInvalid,
                "The sync cursor is outside the current server message range."),
            SyncOperationStatus.AuthenticationUnavailable => AuthenticationRequired(context),
            _ => throw new InvalidOperationException("Unknown sync operation result."),
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
