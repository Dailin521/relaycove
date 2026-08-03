using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddDbContext<RelayCoveDbContext>(options => options.UseSqlite(connectionString));
builder.Services.Configure<PasswordHasherOptions>(options =>
{
    options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
    options.IterationCount = 100_000;
});
builder.Services.AddSingleton<UserNameNormalizer>();
builder.Services.AddSingleton<RefreshTokenHasher>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<PasswordService>();

var app = builder.Build();

app.Logger.LogInformation("RelayCove Server is starting.");

app.Run();

public partial class Program
{
}
