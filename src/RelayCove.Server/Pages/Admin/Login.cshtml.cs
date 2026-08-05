using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RelayCove.Server.Authentication;
using RelayCove.Server.Services;

namespace RelayCove.Server.Pages;

public sealed class LoginModel(WebAdminLoginService loginService) : PageModel
{
    private const string InvalidCredentialsMessage = "账号或密码不正确，或没有管理权限。";

    [BindProperty]
    public string? UserName { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var login = await loginService.LoginAsync(UserName, Password, cancellationToken);
        if (login is null)
        {
            Password = null;
            ModelState.Remove(nameof(Password));
            ModelState.AddModelError(string.Empty, InvalidCredentialsMessage);
            return Page();
        }

        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, login.UserId.ToString("D")),
            new(ClaimTypes.Name, login.DisplayName),
            new("atv", login.AccessTokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];
        var identity = new ClaimsIdentity(claims, WebAdminAuthenticationDefaults.Scheme);
        await HttpContext.SignInAsync(
            WebAdminAuthenticationDefaults.Scheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
            });
        return RedirectToPage("/Admin");
    }
}
