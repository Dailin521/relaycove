using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Errors;
using RelayCove.Server.Hubs;
using RelayCove.Server.Services;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Authentication;

public sealed class RelayCoveJwtBearerEvents(
    RelayCoveDbContext dbContext,
    ILogger<RelayCoveJwtBearerEvents> logger) : JwtBearerEvents
{
    public override Task MessageReceived(MessageReceivedContext context)
    {
        var accessToken = context.Request.Query["access_token"];
        if (accessToken.Count == 1 &&
            !string.IsNullOrWhiteSpace(accessToken[0]) &&
            context.HttpContext.Request.Path.StartsWithSegments(ChatHub.Route))
        {
            context.Token = accessToken[0];
        }

        return Task.CompletedTask;
    }

    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var subject = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var userId) || userId == Guid.Empty)
        {
            context.Fail("The access token subject is invalid.");
            return;
        }

        var rawTokenVersion = context.Principal?.FindFirst(AccessTokenService.AccessTokenVersionClaimType)?.Value;
        var tokenVersion = 0L;
        if (rawTokenVersion is not null &&
            (!long.TryParse(rawTokenVersion, NumberStyles.None, CultureInfo.InvariantCulture, out tokenVersion) || tokenVersion < 0))
        {
            context.Fail("The access token version is invalid.");
            return;
        }

        var activeUser = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && !user.IsDisabled && user.RetiredAt == null)
            .Select(user => new { user.AccessTokenVersion })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        if (activeUser is null || activeUser.AccessTokenVersion != tokenVersion)
        {
            logger.LogInformation("Access token rejected because user {UserId} is inactive or its version is stale.", userId);
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
