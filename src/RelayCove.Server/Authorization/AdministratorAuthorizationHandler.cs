using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;

namespace RelayCove.Server.Authorization;

public sealed class AdministratorAuthorizationHandler(RelayCoveDbContext dbContext)
    : AuthorizationHandler<AdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdministratorRequirement requirement)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var userId) || userId == Guid.Empty)
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext
            ? httpContext.RequestAborted
            : CancellationToken.None;
        var isAdministrator = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Id == userId && !user.IsDisabled && user.IsAdmin,
                cancellationToken);
        if (isAdministrator)
        {
            context.Succeed(requirement);
        }
    }
}
