using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using RelayCove.Server.Errors;
using RelayCove.Server.Realtime;
using RelayCove.Server.Services;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Endpoints;

public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/messages", SendAsync).RequireAuthorization();
        endpoints.MapGet("/api/conversations/{conversationId:guid}/messages", GetHistoryAsync)
            .RequireAuthorization();
        endpoints.MapGet(
                "/api/conversations/{conversationId:guid}/messages/around/{messageId:long}",
                GetAroundAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/conversations/{conversationId:guid}/read", MarkReadAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> SendAsync(
        [FromBody] SendMessageRequest? request,
        HttpContext context,
        MessageRequestValidator validator,
        MessageCommandService commandService,
        NewMessagePublisher newMessagePublisher,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.ValidateSend(request);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await commandService.SendAsync(actorUserId, request!, cancellationToken);
        if (result.Status == MessageOperationStatus.Created)
        {
            await newMessagePublisher.TryPublishAsync(result.Value!);
        }

        return result.Status switch
        {
            MessageOperationStatus.Created => Results.Created(
                $"/api/conversations/{result.Value!.ConversationId:D}/messages/{result.Value.Id}",
                result.Value),
            MessageOperationStatus.Replay => Results.Ok(result.Value),
            _ => MessageError(context, result.Status),
        };
    }

    private static async Task<IResult> GetHistoryAsync(
        Guid conversationId,
        [FromQuery] long? beforeMessageId,
        [FromQuery] int? limit,
        HttpContext context,
        MessageRequestValidator validator,
        MessageQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.ValidateHistory(beforeMessageId, limit);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await queryService.GetHistoryAsync(
            actorUserId,
            conversationId,
            beforeMessageId,
            limit ?? 50,
            cancellationToken);
        return result.Status == MessageOperationStatus.Success
            ? Results.Ok(result.Value)
            : MessageError(context, result.Status);
    }

    private static async Task<IResult> MarkReadAsync(
        Guid conversationId,
        [FromBody] MarkConversationReadRequest? request,
        HttpContext context,
        MessageRequestValidator validator,
        MessageReadService readService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.ValidateRead(request);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await readService.MarkReadAsync(
            actorUserId,
            conversationId,
            request!.MessageId,
            cancellationToken);
        return result.Status == MessageOperationStatus.Success
            ? Results.Ok(result.Value)
            : MessageError(context, result.Status);
    }

    private static async Task<IResult> GetAroundAsync(
        Guid conversationId,
        long messageId,
        [FromQuery] int? before,
        [FromQuery] int? after,
        HttpContext context,
        MessageRequestValidator validator,
        MessageQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.ValidateAround(messageId, before, after);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await queryService.GetAroundAsync(
            actorUserId,
            conversationId,
            messageId,
            before ?? MessageRequestValidator.DefaultAroundSideCount,
            after ?? MessageRequestValidator.DefaultAroundSideCount,
            cancellationToken);
        return result.Status == MessageOperationStatus.Success
            ? Results.Ok(result.Value)
            : MessageError(context, result.Status);
    }

    private static IResult MessageError(HttpContext context, MessageOperationStatus status) =>
        status switch
        {
            MessageOperationStatus.InvalidRequest => ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid."),
            MessageOperationStatus.AccessRevoked => ApiErrorWriter.Result(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.ConversationAccessRevoked,
                "Conversation access is unavailable."),
            MessageOperationStatus.MessageTypeUnsupported => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.MessageTypeUnsupported,
                "The message type is not supported by this endpoint."),
            MessageOperationStatus.ReplyInvalid => ValidationError(
                context,
                new Dictionary<string, string[]>
                {
                    ["replyToMessageId"] = ["The reply message is unavailable in this conversation."],
                }),
            MessageOperationStatus.MentionInvalid => ValidationError(
                context,
                new Dictionary<string, string[]>
                {
                    ["mentionUserIds"] = ["One or more mentioned users are unavailable in this conversation."],
                }),
            MessageOperationStatus.AttachmentInvalid => ValidationError(
                context,
                new Dictionary<string, string[]>
                {
                    ["attachmentIds"] = ["One or more attachments are unavailable for this message."],
                }),
            MessageOperationStatus.MessageTargetInvalid => ValidationError(
                context,
                new Dictionary<string, string[]>
                {
                    ["messageId"] = ["The message is unavailable in this conversation."],
                }),
            MessageOperationStatus.IdempotencyKeyReuse => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.IdempotencyKeyReuse,
                "The client message ID is already used by a different payload."),
            _ => throw new InvalidOperationException("Unknown message operation result."),
        };

    private static IResult ValidationError(
        HttpContext context,
        IReadOnlyDictionary<string, string[]> errors) =>
        ApiErrorWriter.Result(
            context,
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.ValidationFailed,
            "The request is invalid.",
            errors);

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
