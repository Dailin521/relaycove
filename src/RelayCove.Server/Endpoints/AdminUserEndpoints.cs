using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using RelayCove.Server.Authorization;
using RelayCove.Server.Errors;
using RelayCove.Server.Realtime;
using RelayCove.Server.Services;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/users")
            .RequireAuthorization(AuthorizationPolicies.Administrator);
        group.MapGet(string.Empty, ListUsersAsync);
        group.MapPost(string.Empty, CreateUserAsync);
        group.MapPut("/{userId:guid}", UpdateUserAsync);
        group.MapPost("/{userId:guid}/reset-password", ResetPasswordAsync);
        group.MapDelete("/{userId:guid}", RetireUserAsync);
        return endpoints;
    }

    private static async Task<IResult> ListUsersAsync(
        AdminUserService adminUserService,
        CancellationToken cancellationToken) =>
        Results.Ok(await adminUserService.ListUsersAsync(cancellationToken));

    private static async Task<IResult> CreateUserAsync(
        [FromBody] CreateUserRequest? request,
        HttpContext context,
        NewUserValidator newUserValidator,
        AdminUserService adminUserService,
        CancellationToken cancellationToken)
    {
        var errors = newUserValidator.Validate(request?.UserName, request?.DisplayName, request?.Password);
        if (errors.Count > 0)
        {
            return ValidationError(context, errors);
        }

        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await adminUserService.CreateUserAsync(actorUserId, request!, cancellationToken);
        return result.Status switch
        {
            AdminUserCreationStatus.Created => Results.Created(
                $"/api/admin/users/{result.User!.UserId:D}",
                result.User),
            AdminUserCreationStatus.UserNameAlreadyExists => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.UserNameAlreadyExists,
                "The user name is already in use."),
            AdminUserCreationStatus.ActorNotAdministrator => AccessDenied(context),
            _ => throw new InvalidOperationException("Unknown administrator user creation result."),
        };
    }

    private static async Task<IResult> UpdateUserAsync(
        Guid userId,
        [FromBody] UpdateAdminUserRequest? request,
        HttpContext context,
        AdminUserService adminUserService,
        AccountAccessRevokedPublisher accountAccessRevokedPublisher,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ValidationError(context, new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["request"] = ["A request body is required."],
            });
        }

        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await adminUserService.UpdateDisabledAsync(
            actorUserId,
            userId,
            request.IsDisabled,
            cancellationToken);
        await PublishRevocationAfterCommitAsync(result, accountAccessRevokedPublisher);
        return result.Status is AdminUserMutationStatus.Updated or AdminUserMutationStatus.Unchanged
            ? Results.Ok(result.User)
            : MutationError(context, result);
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid userId,
        [FromBody] ResetUserPasswordRequest? request,
        HttpContext context,
        AdminUserService adminUserService,
        AccountAccessRevokedPublisher accountAccessRevokedPublisher,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await adminUserService.ResetPasswordAsync(
            actorUserId,
            userId,
            request?.Password,
            cancellationToken);
        await PublishRevocationAfterCommitAsync(result, accountAccessRevokedPublisher);
        return result.Status == AdminUserMutationStatus.PasswordReset
            ? Results.NoContent()
            : MutationError(context, result);
    }

    private static async Task<IResult> RetireUserAsync(
        Guid userId,
        HttpContext context,
        AdminUserService adminUserService,
        AccountAccessRevokedPublisher accountAccessRevokedPublisher,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorUserId(context, out var actorUserId))
        {
            return AuthenticationRequired(context);
        }

        var result = await adminUserService.RetireAsync(actorUserId, userId, cancellationToken);
        await PublishRevocationAfterCommitAsync(result, accountAccessRevokedPublisher);
        return result.Status == AdminUserMutationStatus.Retired
            ? Results.NoContent()
            : MutationError(context, result);
    }

    private static async Task PublishRevocationAfterCommitAsync(
        AdminUserMutationResult result,
        AccountAccessRevokedPublisher accountAccessRevokedPublisher)
    {
        if (result.RequiresAccessRevocation &&
            result.User is not null &&
            result.MinimumAccessTokenVersion is long minimumAccessTokenVersion)
        {
            await accountAccessRevokedPublisher.TryPublishAsync(
                result.User.UserId,
                minimumAccessTokenVersion);
        }
    }

    private static IResult MutationError(HttpContext context, AdminUserMutationResult result) =>
        result.Status switch
        {
            AdminUserMutationStatus.ValidationFailed => ValidationError(context, result.ValidationErrors!),
            AdminUserMutationStatus.ActorNotAdministrator => AccessDenied(context),
            AdminUserMutationStatus.SelfActionForbidden => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.SelfActionForbidden,
                "Administrators cannot disable or retire their own account."),
            AdminUserMutationStatus.UserNotFound => ApiErrorWriter.Result(
                context,
                StatusCodes.Status404NotFound,
                ApiErrorCodes.UserNotFound,
                "The user was not found."),
            AdminUserMutationStatus.UserRetired => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.UserRetired,
                "The user has been retired."),
            AdminUserMutationStatus.LastActiveAdministrator => ApiErrorWriter.Result(
                context,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.LastActiveAdministrator,
                "The last active administrator cannot be disabled or retired."),
            _ => throw new InvalidOperationException("Unknown administrator user mutation result."),
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

    private static IResult AccessDenied(HttpContext context) =>
        ApiErrorWriter.Result(
            context,
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.AccessDenied,
            "Access is denied.");

    private static bool TryGetActorUserId(HttpContext context, out Guid actorUserId)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParseExact(subject, "D", out actorUserId) && actorUserId != Guid.Empty;
    }
}
