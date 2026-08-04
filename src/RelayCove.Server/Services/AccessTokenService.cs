using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Options;

namespace RelayCove.Server.Services;

public sealed class AccessTokenService
{
    public const string AccessTokenType = "at+jwt";
    public const string AccessTokenVersionClaimType = "atv";
    private readonly AuthenticationOptions options;
    private readonly ServerClock clock;
    private readonly JwtSecurityTokenHandler tokenHandler = new();
    private readonly SigningCredentials signingCredentials;

    public AccessTokenService(IOptions<AuthenticationOptions> options, ServerClock clock)
    {
        this.options = options.Value;
        this.clock = clock;
        var signingKeyBytes = AuthenticationOptionsValidator.DecodeSigningKey(this.options.SigningKey);
        signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(signingKeyBytes),
            SecurityAlgorithms.HmacSha256);
    }

    public AccessTokenResult CreateToken(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var issuedAt = clock.UtcNow;
        var expiresAt = issuedAt.AddMinutes(options.AccessTokenMinutes);
        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D").ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D").ToLowerInvariant()),
            new(AccessTokenVersionClaimType, user.AccessTokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        ];
        var payload = new JwtPayload(
            options.Issuer,
            options.Audience,
            claims,
            issuedAt,
            expiresAt,
            issuedAt);
        var header = new JwtHeader(signingCredentials);
        header[JwtHeaderParameterNames.Typ] = AccessTokenType;
        var token = new JwtSecurityToken(header, payload);

        return new AccessTokenResult(tokenHandler.WriteToken(token), expiresAt);
    }
}
