using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Errors;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Authentication;

public sealed class RelayCoveJwtBearerEvents(
    RelayCoveDbContext dbContext,
    ILogger<RelayCoveJwtBearerEvents> logger) : JwtBearerEvents
{
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var subject = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var userId) || userId == Guid.Empty)
        {
            context.Fail("The access token subject is invalid.");
            return;
        }

        var activeUserExists = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == userId && !user.IsDisabled, context.HttpContext.RequestAborted);
        if (!activeUserExists)
        {
            logger.LogInformation("Access token rejected because user {UserId} is missing or disabled.", userId);
            context.Fail("The access token subject is not active.");
        }
    }

    public override Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
        return ApiErrorWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            ApiErrorCodes.AuthenticationRequired,
            "Authentication is required.",
            cancellationToken: context.HttpContext.RequestAborted);
    }

    public override Task Forbidden(ForbiddenContext context)
    {
        return ApiErrorWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.AccessDenied,
            "Access is denied.",
            cancellationToken: context.HttpContext.RequestAborted);
    }
}
