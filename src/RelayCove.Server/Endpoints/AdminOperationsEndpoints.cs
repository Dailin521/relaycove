using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using RelayCove.Server.Authorization;
using RelayCove.Server.Errors;
using RelayCove.Server.Services;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class AdminOperationsEndpoints
{
    public static IEndpointRouteBuilder MapAdminOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin")
            .RequireAuthorization(AuthorizationPolicies.Administrator);
        group.MapGet("/channels", ListChannelsAsync);
        group.MapGet("/status", GetStatusAsync);
        group.MapGet("/settings/upload", GetUploadSettingsAsync);
        group.MapPut("/settings/upload", UpdateUploadSettingsAsync);
        return endpoints;
    }

    private static async Task<IResult> ListChannelsAsync(
        AdminOperationsService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListChannelsAsync(cancellationToken));

    private static async Task<IResult> GetStatusAsync(
        AdminOperationsService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetStatusAsync(cancellationToken));

    private static async Task<IResult> GetUploadSettingsAsync(
        UploadSettingsService service,
        CancellationToken cancellationToken) =>
        Results.Ok(new UploadSettingsResponse(
            await service.GetEffectiveMaximumFileBytesAsync(cancellationToken)));

    private static async Task<IResult> UpdateUploadSettingsAsync(
        [FromBody] UpdateUploadSettingsRequest? request,
        HttpContext context,
        UploadSettingsService service,
        CancellationToken cancellationToken)
    {
        if (request is null || !UploadSettingsService.IsValidMaximumFileBytes(request.MaximumFileBytes))
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid.",
                new Dictionary<string, string[]>
                {
                    ["maximumFileBytes"] = ["The attachment limit must be between 1 MiB and 100 MiB."],
                });
        }

        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var actorUserId) || actorUserId == Guid.Empty)
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.AuthenticationRequired,
                "Authentication is required.");
        }

        var result = await service.SetEffectiveMaximumFileBytesAsync(
            actorUserId,
            request.MaximumFileBytes,
            cancellationToken);
        return result.Status switch
        {
            UploadSettingsUpdateStatus.Success => Results.Ok(
                new UploadSettingsResponse(result.EffectiveMaximumFileBytes)),
            UploadSettingsUpdateStatus.AccessDenied => ApiErrorWriter.Result(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.AccessDenied,
                "Access is denied."),
            UploadSettingsUpdateStatus.InvalidRequest => ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid."),
            _ => throw new InvalidOperationException("Unknown upload settings update result."),
        };
    }
}
