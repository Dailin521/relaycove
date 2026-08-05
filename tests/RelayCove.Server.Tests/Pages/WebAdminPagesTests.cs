using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Tests.Pages;

public sealed class WebAdminPagesTests
{
    private const string Password = "a secure web administrator password";

    [Fact]
    public async Task AdminPage_WhenUnauthenticated_RedirectsToLogin()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        using var client = CreateBrowser(factory);

        using var response = await client.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WhenAdministrator_SetsRestrictedCookieAndCannotAuthenticateApi()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"web-admin-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var client = CreateBrowser(factory);

        var token = await GetAntiforgeryTokenAsync(client, "/admin/login");
        using var response = await PostFormAsync(client, "/admin/login", token, new Dictionary<string, string>
        {
            ["UserName"] = userName,
            ["Password"] = Password,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("RelayCove.WebAdmin=", StringComparison.Ordinal));
        Assert.Contains("path=/admin", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        using var dashboard = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        using var api = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task Login_WhenOrdinaryOrInvalidUser_ReturnsSameFailureMessage()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"web-user-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password);
        using var client = CreateBrowser(factory);

        var ordinary = await LoginFailureBodyAsync(client, userName, Password);
        var missing = await LoginFailureBodyAsync(client, "missing-user", Password);

        Assert.Contains("账号或密码不正确，或没有管理权限。", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, ordinary, StringComparison.Ordinal);
        Assert.Equal(ordinary.Contains("账号或密码不正确，或没有管理权限。", StringComparison.Ordinal), missing.Contains("账号或密码不正确，或没有管理权限。", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminPost_WhenAntiforgeryMissing_IsRejectedAndValidTokenCreatesChannel()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"web-write-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var client = CreateBrowser(factory);
        await LoginAsync(client, userName);

        using (var missing = await client.PostAsync("/admin?handler=CreateChannel", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "blocked channel",
            ["isPrivate"] = "false",
        })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }

        var token = await GetAntiforgeryTokenAsync(client, "/admin");
        using var created = await PostFormAsync(client, "/admin?handler=CreateChannel", token, new Dictionary<string, string>
        {
            ["name"] = "browser channel",
            ["isPrivate"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var dashboard = await client.GetAsync("/admin");
        Assert.Contains("browser channel", await dashboard.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminCookie_WhenAccountDisabled_IsImmediatelyRejected()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"web-revoke-{Guid.NewGuid():N}";
        var userId = await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var client = CreateBrowser(factory);
        await LoginAsync(client, userName);
        await factory.SetUserDisabledAsync(userId, true);

        using var response = await client.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task AdminCookie_WhenPasswordResetChangesTokenVersion_IsImmediatelyRejected()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"web-reset-{Guid.NewGuid():N}";
        var userId = await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var client = CreateBrowser(factory);
        await LoginAsync(client, userName);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AdminUserService>();
            var result = await service.ResetPasswordAsync(
                userId,
                userId,
                "a different secure administrator password",
                CancellationToken.None);
            Assert.Equal(AdminUserMutationStatus.PasswordReset, result.Status);
        }

        using var response = await client.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task BearerToken_WhenSentToAdminPage_DoesNotAuthenticateBrowserPanel()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"web-scheme-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var client = CreateBrowser(factory);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, Password, "test", "1.0.0"));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var page = await client.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
    }

    [Fact]
    public async Task AdminPanel_WhenPathBaseConfigured_UsesPrefixForRoutesAndCookiePath()
    {
        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["RelayCove:PathBase"] = "/relaycove",
            });
        await factory.InitializeDatabaseAsync();
        var userName = $"web-pathbase-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var client = CreateBrowser(factory);

        var token = await GetAntiforgeryTokenAsync(client, "/relaycove/admin/login");
        using var login = await PostFormAsync(client, "/relaycove/admin/login", token, new Dictionary<string, string>
        {
            ["UserName"] = userName,
            ["Password"] = Password,
        });

        Assert.Equal("/relaycove/admin", login.Headers.Location!.OriginalString);
        var cookie = Assert.Single(
            login.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("RelayCove.WebAdmin=", StringComparison.Ordinal));
        Assert.Contains("path=/relaycove/admin", cookie, StringComparison.OrdinalIgnoreCase);
        using var dashboard = await client.GetAsync("/relaycove/admin/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var html = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/relaycove/admin?handler=", html, StringComparison.Ordinal);
        Assert.Contains("最近错误", html, StringComparison.Ordinal);
        Assert.Contains("no-store", dashboard.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", dashboard.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginPage_WhenRateLimitIsReached_RejectsAdditionalRequests()
    {
        using var factory = new RelayCoveWebApplicationFactory(loginPermitLimit: 2, refreshPermitLimit: 10);
        await factory.InitializeDatabaseAsync();
        using var client = CreateBrowser(factory);

        var token = await GetAntiforgeryTokenAsync(client, "/admin/login");
        using var first = await PostFormAsync(client, "/admin/login", token, new Dictionary<string, string>
        {
            ["UserName"] = "not-found",
            ["Password"] = Password,
        });
        using var second = await PostFormAsync(client, "/admin/login", token, new Dictionary<string, string>
        {
            ["UserName"] = "not-found",
            ["Password"] = Password,
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task AdminPanel_Posts_CreateUserUpdateSettingsAndManagePrivateMembers()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var adminName = $"web-operations-{Guid.NewGuid():N}";
        var memberName = $"web-member-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(adminName, Password, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, Password);
        using var client = CreateBrowser(factory);
        await LoginAsync(client, adminName);

        var token = await GetAntiforgeryTokenAsync(client, "/admin");
        using (var createdUser = await PostFormAsync(client, "/admin?handler=CreateUser", token, new Dictionary<string, string>
        {
            ["userName"] = "created-from-browser",
            ["displayName"] = "页面创建用户",
            ["password"] = Password,
            ["isAdmin"] = "false",
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, createdUser.StatusCode);
        }

        token = await GetAntiforgeryTokenAsync(client, "/admin");
        using (var updateSettings = await PostFormAsync(client, "/admin?handler=UpdateUpload", token, new Dictionary<string, string>
        {
            ["maximumMiB"] = "2",
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, updateSettings.StatusCode);
        }

        token = await GetAntiforgeryTokenAsync(client, "/admin");
        using (var createChannel = await PostFormAsync(client, "/admin?handler=CreateChannel", token, new Dictionary<string, string>
        {
            ["name"] = "member browser channel",
            ["isPrivate"] = "true",
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, createChannel.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var channelId = await dbContext.Conversations
            .Where(conversation => conversation.Name == "member browser channel")
            .Select(conversation => conversation.Id)
            .SingleAsync();
        Assert.True(await dbContext.Users.AnyAsync(user => user.UserName == "created-from-browser"));
        var settings = scope.ServiceProvider.GetRequiredService<UploadSettingsService>();
        Assert.Equal(2L * 1024 * 1024, await settings.GetEffectiveMaximumFileBytesAsync(CancellationToken.None));

        token = await GetAntiforgeryTokenAsync(client, "/admin");
        using var addMember = await PostFormAsync(client, "/admin?handler=AddMember", token, new Dictionary<string, string>
        {
            ["conversationId"] = channelId.ToString("D"),
            ["userId"] = memberId.ToString("D"),
            ["isChannelAdmin"] = "false",
        });
        Assert.Equal(HttpStatusCode.Redirect, addMember.StatusCode);
        Assert.True(await dbContext.ConversationMembers.AnyAsync(member => member.ConversationId == channelId && member.UserId == memberId));

        token = await GetAntiforgeryTokenAsync(client, "/admin");
        using (var duplicateUser = await PostFormAsync(client, "/admin?handler=CreateUser", token, new Dictionary<string, string>
        {
            ["userName"] = "created-from-browser",
            ["displayName"] = "重复用户",
            ["password"] = Password,
            ["isAdmin"] = "false",
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, duplicateUser.StatusCode);
        }

        using var feedback = await client.GetAsync("/admin");
        var feedbackHtml = await feedback.Content.ReadAsStringAsync();
        var feedbackMatch = Regex.Match(
            feedbackHtml,
            "<div class=\"flash [^\"]+\" role=\"status\">([^<]+)</div>",
            RegexOptions.CultureInvariant);
        Assert.True(feedbackMatch.Success, "The administrator feedback message was not rendered.");
        Assert.Contains(
            "账号已存在",
            WebUtility.HtmlDecode(feedbackMatch.Groups[1].Value),
            StringComparison.Ordinal);
    }

    private static HttpClient CreateBrowser(RelayCoveWebApplicationFactory factory) => factory.CreateClient(new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    });

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/admin/login");
        using var response = await PostFormAsync(client, "/admin/login", token, new Dictionary<string, string>
        {
            ["UserName"] = userName,
            ["Password"] = Password,
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> LoginFailureBodyAsync(HttpClient client, string userName, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/admin/login");
        using var response = await PostFormAsync(client, "/admin/login", token, new Dictionary<string, string>
        {
            ["UserName"] = userName,
            ["Password"] = password,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : throw new InvalidOperationException("Antiforgery token was not rendered.");
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string token, Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = token;
        return client.PostAsync(path, new FormUrlEncodedContent(values));
    }
}
