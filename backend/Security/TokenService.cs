// Issue configured HS256 access tokens and share strict validation policy with middleware.
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UserManagement.Api;
using UserManagement.Domain;
namespace UserManagement.Security;
public interface ITokenService { LoginResult Issue(UserRecord user); }
public sealed class TokenService(SecurityOptions options, TimeProvider clock) : ITokenService
{
    // Mint only for active supported identities and include persisted credential version.
    public LoginResult Issue(UserRecord user)
    {
        if (!user.IsActive || !Enum.IsDefined(user.Role) || user.CredentialVersion < 1) throw new ApiException(401, "authentication_failed", "Credentials could not be verified");
        var now = clock.GetUtcNow().UtcDateTime;
        var expiry = now.AddMinutes(15);
        var token = new JwtSecurityToken(options.Issuer, options.Audience,
            [new Claim("sub", user.Id.ToString()), new Claim("role", user.Role.ToString()), new Claim("cv", user.CredentialVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            now, expiry, new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSecret)), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", token.ValidTo);
    }
    // Validate every signed-token boundary with zero clock skew and no alternate algorithms.
    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSecret)),
        ValidateIssuer = true, ValidIssuer = options.Issuer, ValidateAudience = true, ValidAudience = options.Audience,
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], NameClaimType = "sub", RoleClaimType = "role"
    };
}
public sealed record LoginResult(string AccessToken, string TokenType, DateTime ExpiresAt);
