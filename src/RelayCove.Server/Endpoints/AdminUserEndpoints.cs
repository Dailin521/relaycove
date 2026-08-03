using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using RelayCove.Server.Authorization;
using RelayCove.Server.Errors;
using RelayCove.Server.Services;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/users", CreateUserAsync)
            .RequireAuthorization(AuthorizationPolicies.Administrator);
        return endpoints;
    }

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
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid.",
                errors);
        }

        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var actorUserId))
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.AuthenticationRequired,
                "Authentication is required.");
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
            AdminUserCreationStatus.ActorNotAdministrator => ApiErrorWriter.Result(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.AccessDenied,
                "Access is denied."),
            _ => throw new InvalidOperationException("Unknown administrator user creation result."),
        };
    }
}
