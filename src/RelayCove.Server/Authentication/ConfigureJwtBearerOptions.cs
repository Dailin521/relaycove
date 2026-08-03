using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RelayCove.Server.Options;
using RelayCove.Server.Services;

namespace RelayCove.Server.Authentication;

public sealed class ConfigureJwtBearerOptions(IOptions<AuthenticationOptions> authenticationOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(JwtBearerDefaults.AuthenticationScheme, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name is not JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        var configuredOptions = authenticationOptions.Value;
        var signingKeyBytes = AuthenticationOptionsValidator.DecodeSigningKey(configuredOptions.SigningKey);

        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.SaveToken = false;
        options.EventsType = typeof(RelayCoveJwtBearerEvents);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ClockSkew = TimeSpan.FromSeconds(configuredOptions.ClockSkewSeconds),
            IssuerSigningKey = new SymmetricSecurityKey(signingKeyBytes),
            NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidAudience = configuredOptions.Audience,
            ValidIssuer = configuredOptions.Issuer,
            ValidTypes = [AccessTokenService.AccessTokenType],
        };
    }
}
