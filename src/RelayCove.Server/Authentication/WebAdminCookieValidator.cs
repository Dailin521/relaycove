using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;

namespace RelayCove.Server.Authentication;

public static class WebAdminCookieValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var subject = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var version = context.Principal?.FindFirst("atv")?.Value;
        if (!Guid.TryParseExact(subject, "D", out var userId) ||
            !long.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var accessTokenVersion))
        {
            await RejectAsync(context);
            return;
        }

        var dbContext = context.HttpContext.RequestServices.GetRequiredService<RelayCoveDbContext>();
        var valid = await dbContext.Users.AsNoTracking().AnyAsync(
            user => user.Id == userId &&
                user.IsAdmin &&
                !user.IsDisabled &&
                user.RetiredAt == null &&
                user.AccessTokenVersion == accessTokenVersion,
            context.HttpContext.RequestAborted);
        if (!valid)
        {
            await RejectAsync(context);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(WebAdminAuthenticationDefaults.Scheme);
    }
}
