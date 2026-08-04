using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using RelayCove.Server.Errors;
using RelayCove.Server.Realtime;
using RelayCove.Server.Services;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/conversations").RequireAuthorization();
        group.MapGet(string.Empty, ListAsync);
        group.MapPost(string.Empty, CreateAsync);
        group.MapGet("/{conversationId:guid}", GetAsync);
        group.MapPut("/{conversationId:guid}", UpdateAsync);
        group.MapDelete("/{conversationId:guid}", DeleteAsync);
        group.MapGet("/{conversationId:guid}/members", ListMembersAsync);
        group.MapGet(
            "/{conversationId:guid}/mention-candidates",
            ListMentionCandidatesAsync);
        group.MapPost("/{conversationId:guid}/members", UpsertMemberAsync);
        group.MapDelete("/{conversationId:guid}/members/{userId:guid}", RemoveMemberAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ConversationQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await queryService.ListAsync(actorUserId, cancellationToken);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetAsync(
        Guid conversationId,
        HttpContext context,
        ConversationQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await queryService.GetAsync(actorUserId, conversationId, cancellationToken);
        return result.Status == ConversationOperationStatus.Success
            ? Results.Ok(result.Value)
            : ConversationError(context, result.Status);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateConversationRequest? request,
        HttpContext context,
        ConversationRequestValidator validator,
        ConversationCommandService commandService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.ValidateCreate(request, actorUserId);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await commandService.CreateAsync(actorUserId, request!, cancellationToken);
        return result.Status switch
        {
            ConversationOperationStatus.Created => Results.Created(
                $"/api/conversations/{result.Value!.Id:D}",
                result.Value),
            ConversationOperationStatus.Success => Results.Ok(result.Value),
            _ => ConversationError(context, result.Status),
        };
    }

    private static async Task<IResult> ListMembersAsync(
        Guid conversationId,
        HttpContext context,
        ConversationQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await queryService.ListMembersAsync(actorUserId, conversationId, cancellationToken);
        return result.Status == ConversationOperationStatus.Success
            ? Results.Ok(result.Value)
            : ConversationError(context, result.Status);
    }

    private static async Task<IResult> UpdateAsync(
        Guid conversationId,
        [FromBody] UpdateConversationRequest? request,
        HttpContext context,
        ConversationRequestValidator validator,
        ConversationCommandService commandService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.ValidateUpdate(request);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await commandService.UpdateChannelAsync(
            actorUserId,
            conversationId,
            request!,
            cancellationToken);
        return result.Status == ConversationOperationStatus.Success
            ? Results.Ok(result.Value)
            : ConversationError(context, result.Status);
    }

    private static async Task<IResult> DeleteAsync(
        Guid conversationId,
        HttpContext context,
        ConversationCommandService commandService,
        ConversationAccessRevokedPublisher accessRevokedPublisher,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await commandService.DeleteChannelAsync(
            actorUserId,
            conversationId,
            cancellationToken);
        if (result.Status == ConversationOperationStatus.NoContent)
        {
            foreach (var userId in result.RevokedUserIds!)
            {
                await accessRevokedPublisher.TryPublishAsync(userId, conversationId);
            }

            return Results.NoContent();
        }

        return ConversationError(context, result.Status);
    }

    private static async Task<IResult> ListMentionCandidatesAsync(
        Guid conversationId,
        [FromQuery] string? query,
        [FromQuery] int? limit,
        HttpContext context,
        MentionCandidateQueryValidator validator,
        MentionCandidateQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.Validate(query, limit);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await queryService.ListAsync(
            actorUserId,
            conversationId,
            query!,
            limit ?? MentionCandidateQueryValidator.DefaultLimit,
            cancellationToken);
        return result.Status == ConversationOperationStatus.Success
            ? Results.Ok(result.Value)
            : ConversationError(context, result.Status);
    }

    private static async Task<IResult> UpsertMemberAsync(
        Guid conversationId,
        [FromBody] UpsertConversationMemberRequest? request,
        HttpContext context,
        ConversationRequestValidator validator,
        ConversationCommandService commandService,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var errors = validator.ValidateMember(request);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        var result = await commandService.UpsertMemberAsync(
            actorUserId,
            conversationId,
            request!,
            cancellationToken);
        return result.Status switch
        {
            ConversationOperationStatus.Created => Results.Created(
                $"/api/conversations/{conversationId:D}/members/{result.Value!.UserId:D}",
                result.Value),
            ConversationOperationStatus.Success => Results.Ok(result.Value),
            _ => ConversationError(context, result.Status),
        };
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid conversationId,
        Guid userId,
        HttpContext context,
        ConversationCommandService commandService,
        ConversationAccessRevokedPublisher accessRevokedPublisher,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await commandService.RemoveMemberWithResultAsync(
            actorUserId,
            conversationId,
            userId,
            cancellationToken);
        if (result.Status == ConversationOperationStatus.NoContent &&
            result.RemovedUserId is Guid removedUserId)
        {
            await accessRevokedPublisher.TryPublishAsync(removedUserId, conversationId);
        }

        return result.Status == ConversationOperationStatus.NoContent
            ? Results.NoContent()
            : ConversationError(context, result.Status);
    }

    private static IResult ConversationError(HttpContext context, ConversationOperationStatus status) =>
        status switch
        {
            ConversationOperationStatus.InvalidRequest => ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid."),
            ConversationOperationStatus.AccessDenied => ApiErrorWriter.Result(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.AccessDenied,
                "Access is denied."),
            ConversationOperationStatus.AccessRevoked => ApiErrorWriter.Result(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.ConversationAccessRevoked,
                "Conversation access is unavailable."),
            ConversationOperationStatus.UserNotFound => ApiErrorWriter.Result(
                context,
                StatusCodes.Status404NotFound,
                ApiErrorCodes.UserNotFound,
                "The user was not found."),
            ConversationOperationStatus.ConversationTypeConflict => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.ConversationTypeConflict,
                "The conversation type does not support this operation."),
            _ => throw new InvalidOperationException("Unknown conversation operation result."),
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
