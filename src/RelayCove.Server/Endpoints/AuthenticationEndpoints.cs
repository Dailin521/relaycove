using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Errors;
using RelayCove.Server.RateLimiting;
using RelayCove.Server.Services;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Endpoints;

public static class AuthenticationEndpoints
{
    private const int MaximumPasswordLength = 1_024;
    private const int MaximumClientVersionLength = 64;

    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");
        group.MapPost("/login", LoginAsync)
            .RequireRateLimiting(AuthenticationRateLimitPolicies.Login);
        group.MapPost("/refresh", RefreshAsync)
            .RequireRateLimiting(AuthenticationRateLimitPolicies.Refresh);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", MeAsync)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest? request,
        HttpContext context,
        AuthenticationSessionService sessionService,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateLoginRequest(request);
        if (validationErrors.Count > 0)
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid.",
                validationErrors);
        }

        var response = await sessionService.LoginAsync(request!, cancellationToken);
        return response is null
            ? ApiErrorWriter.Result(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.AuthenticationFailed,
                "Authentication failed.")
            : Results.Ok(response);
    }

    private static async Task<IResult> RefreshAsync(
        [FromBody] RefreshTokenRequest? request,
        HttpContext context,
        AuthenticationSessionService sessionService,
        CancellationToken cancellationToken)
    {
        var response = await sessionService.RefreshAsync(request?.RefreshToken, cancellationToken);
        return response is null
            ? ApiErrorWriter.Result(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.AuthenticationFailed,
                "Authentication failed.")
            : Results.Ok(response);
    }

    private static async Task<IResult> LogoutAsync(
        [FromBody] LogoutRequest? request,
        AuthenticationSessionService sessionService,
        CancellationToken cancellationToken)
    {
        await sessionService.LogoutAsync(request?.RefreshToken, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        HttpContext context,
        RelayCoveDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var userId))
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.AuthenticationRequired,
                "Authentication is required.");
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId && !candidate.IsDisabled, cancellationToken);
        if (user is null)
        {
            return ApiErrorWriter.Result(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.AuthenticationRequired,
                "Authentication is required.");
        }

        return Results.Ok(new CurrentUserResponse(user.Id, user.UserName, user.DisplayName, user.IsAdmin));
    }

    private static Dictionary<string, string[]> ValidateLoginRequest(LoginRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors["request"] = ["A request body is required."];
            return errors;
        }

        ValidateRequiredLength(request.UserName, UserNameNormalizer.MaximumLength, "userName", errors);
        ValidateRequiredLength(request.Password, MaximumPasswordLength, "password", errors);
        ValidateRequiredLength(request.DeviceName, 128, "deviceName", errors);
        ValidateRequiredLength(request.ClientVersion, MaximumClientVersionLength, "clientVersion", errors);
        return errors;
    }

    private static void ValidateRequiredLength(
        string? value,
        int maximumLength,
        string fieldName,
        IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[fieldName] = ["The field is required."];
        }
        else if (value.Length > maximumLength)
        {
            errors[fieldName] = [$"The field cannot exceed {maximumLength} characters."];
        }
    }
}
