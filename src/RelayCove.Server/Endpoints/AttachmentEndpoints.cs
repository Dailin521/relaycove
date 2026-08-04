using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RelayCove.Server.Errors;
using RelayCove.Server.Options;
using RelayCove.Server.RateLimiting;
using RelayCove.Server.Services;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/attachments", UploadAsync)
            .RequireAuthorization()
            .RequireRateLimiting(AttachmentRateLimitPolicies.Upload)
            .WithMetadata(new RequestSizeLimitAttribute(
                UploadOptions.AbsoluteMaximumFileBytes + UploadOptions.MultipartOverheadBytes));
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        AttachmentMultipartReader multipartReader,
        AttachmentCommandService commandService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var readResult = await multipartReader.ReadAsync(context.Request, cancellationToken);
        if (readResult.Status == AttachmentUploadReadStatus.InvalidRequest)
        {
            return ValidationError(context);
        }

        if (readResult.Status == AttachmentUploadReadStatus.TooLarge)
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.AttachmentTooLarge,
                "The attachment exceeds the configured size limit.");
        }

        await using var stagedUpload = readResult.Upload!;
        var result = await commandService.CommitAsync(actorUserId, stagedUpload, cancellationToken);
        return result.Status switch
        {
            AttachmentUploadStatus.Created => Results.Created(
                $"/api/attachments/{result.Attachment!.Id:D}",
                result.Attachment),
            AttachmentUploadStatus.ActorUnavailable => AuthenticationRequired(context),
            _ => throw new InvalidOperationException("Unknown attachment upload result."),
        };
    }

    private static IResult ValidationError(HttpContext context) =>
        ApiErrorWriter.Result(
            context,
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.ValidationFailed,
            "The request is invalid.",
            new Dictionary<string, string[]>
            {
                ["file"] = ["Exactly one valid file is required."],
            });

    private static IResult AuthenticationRequired(HttpContext context) =>
        ApiErrorWriter.Result(
            context,
            StatusCodes.Status401Unauthorized,
            ApiErrorCodes.AuthenticationRequired,
            "Authentication is required.");

    private static bool TryGetActorUserId(HttpContext context, out Guid actorUserId)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParseExact(subject, "D", out actorUserId) && actorUserId != Guid.Empty;
    }
}
