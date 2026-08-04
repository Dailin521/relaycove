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
        endpoints.MapGet("/api/attachments/{attachmentId:guid}", GetMetadataAsync)
            .RequireAuthorization();
        endpoints.MapGet("/api/attachments/{attachmentId:guid}/download", DownloadAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> GetMetadataAsync(
        Guid attachmentId,
        HttpContext context,
        AttachmentQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await queryService.GetAsync(actorUserId, attachmentId, cancellationToken);
        if (result.Status != AttachmentAccessStatus.Success)
        {
            return AccessRevoked(context);
        }

        SetPrivateResponseHeaders(context);
        return Results.Ok(result.Value!.Attachment);
    }

    private static async Task<IResult> DownloadAsync(
        Guid attachmentId,
        HttpContext context,
        AttachmentQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await queryService.GetAsync(actorUserId, attachmentId, cancellationToken);
        if (result.Status != AttachmentAccessStatus.Success)
        {
            return AccessRevoked(context);
        }

        var attachment = result.Value!;
        FileStream stream;
        try
        {
            stream = new FileStream(attachment.StoredPath, new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("An authorized attachment file is unavailable.");
        }

        SetPrivateResponseHeaders(context);
        return Results.File(
            stream,
            attachment.ContentType,
            attachment.OriginalFileName,
            enableRangeProcessing: true);
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

    private static IResult AccessRevoked(HttpContext context) =>
        ApiErrorWriter.Result(
            context,
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.ConversationAccessRevoked,
            "Attachment access is unavailable.");

    private static void SetPrivateResponseHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static bool TryGetActorUserId(HttpContext context, out Guid actorUserId)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParseExact(subject, "D", out actorUserId) && actorUserId != Guid.Empty;
    }
}
