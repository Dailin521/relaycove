using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayCove.Server.Authentication;
using RelayCove.Server.Authorization;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Endpoints;
using RelayCove.Server.Errors;
using RelayCove.Server.Hosting;
using RelayCove.Server.Hubs;
using RelayCove.Server.Options;
using RelayCove.Server.RateLimiting;
using RelayCove.Server.Realtime;
using RelayCove.Server.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddDbContext<RelayCoveDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddOptions<AuthenticationOptions>()
    .Bind(builder.Configuration.GetSection(AuthenticationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AuthenticationOptions>, AuthenticationOptionsValidator>();
builder.Services.AddOptions<BootstrapAdminOptions>()
    .Bind(builder.Configuration.GetSection(BootstrapAdminOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BootstrapAdminOptions>, BootstrapAdminOptionsValidator>();
builder.Services.AddOptions<StorageOptions>()
    .Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
builder.Services.AddOptions<UploadOptions>()
    .Bind(builder.Configuration.GetSection(UploadOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<UploadOptions>, UploadOptionsValidator>();
builder.Services.Configure<PasswordHasherOptions>(options =>
{
    options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
    options.IterationCount = 100_000;
});
builder.Services.AddSingleton<UserNameNormalizer>();
builder.Services.AddSingleton<PasswordPolicy>();
builder.Services.AddSingleton<NewUserValidator>();
builder.Services.AddSingleton<ConversationRequestValidator>();
builder.Services.AddSingleton<MentionCandidateQueryValidator>();
builder.Services.AddSingleton<SearchQueryValidator>();
builder.Services.AddSingleton<MessageRequestValidator>();
builder.Services.AddSingleton<SyncRequestValidator>();
builder.Services.AddSingleton<RefreshTokenHasher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerClock>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<AccessTokenService>();
builder.Services.AddScoped<AuthenticationSessionService>();
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<ConversationCommandService>();
builder.Services.AddScoped<ConversationQueryService>();
builder.Services.AddScoped<MentionCandidateQueryService>();
builder.Services.AddScoped<SearchQueryService>();
builder.Services.AddScoped<MessageCommandService>();
builder.Services.AddScoped<MessageQueryService>();
builder.Services.AddScoped<MessageReadService>();
builder.Services.AddScoped<MessageSyncService>();
builder.Services.AddSingleton<AttachmentStoragePaths>();
builder.Services.AddScoped<AttachmentMultipartReader>();
builder.Services.AddScoped<AttachmentCommandService>();
builder.Services.AddScoped<AttachmentQueryService>();
builder.Services.AddScoped<NewMessagePublisher>();
builder.Services.AddScoped<ConversationAccessRevokedPublisher>();
builder.Services.AddSingleton<INewMessageTransport, SignalRNewMessageTransport>();
builder.Services.AddSingleton<
    IConversationAccessRevokedTransport,
    SignalRConversationAccessRevokedTransport>();
builder.Services.AddScoped<RelayCoveJwtBearerEvents>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.Administrator, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(AdministratorRequirement.Instance);
    });
});
builder.Services.AddScoped<IAuthorizationHandler, AdministratorAuthorizationHandler>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddHostedService<BootstrapAdminHostedService>();
builder.Services.AddSingleton<AttachmentStorageRecoveryHostedService>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<AttachmentStorageRecoveryHostedService>());
builder.Services.AddHostedService<AttachmentStorageMaintenanceHostedService>();
builder.Services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>, ConfigureJwtBearerOptions>();
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddSingleton<IConfigureOptions<RateLimiterOptions>, ConfigureAuthenticationRateLimitingOptions>();
builder.Services.AddSingleton<IConfigureOptions<RateLimiterOptions>, ConfigureAttachmentRateLimitingOptions>();
builder.Services.AddSingleton<IConfigureOptions<RateLimiterOptions>, ConfigureSearchRateLimitingOptions>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

var app = builder.Build();

app.Logger.LogInformation("RelayCove Server is starting.");
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapAuthenticationEndpoints();
app.MapAdminUserEndpoints();
app.MapConversationEndpoints();
app.MapSearchEndpoints();
app.MapMessageEndpoints();
app.MapSyncEndpoints();
app.MapAttachmentEndpoints();
app.MapHub<ChatHub>(ChatHub.Route, options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization();

app.Run();

public partial class Program
{
}
