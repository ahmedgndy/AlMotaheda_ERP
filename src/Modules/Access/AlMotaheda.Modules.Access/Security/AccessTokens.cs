using System.Security.Cryptography;
using AlMotaheda.BuildingBlocks.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AlMotaheda.Modules.Access.Security;

/// <summary>
/// Creates the short-lived access tokens: JWTs signed with HMAC-SHA256 that carry the user id, role and
/// permissions, so ordinary requests need no database read.
/// </summary>
internal sealed class AccessTokenIssuer(IOptions<AccessTokenOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <param name="sessionId">The refresh-token family of this sign-in, carried as "sid".</param>
    public IssuedAccessToken Issue(int userId, string roleCode, bool mustChangePassword, Guid sessionId, EffectivePermissions permissions)
    {
        var settings = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(settings.Lifetime);

        var claims = new Dictionary<string, object>
        {
            [AccessClaims.UserId] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AccessClaims.Role] = roleCode,
            [AccessClaims.SessionId] = sessionId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        };

        var permissionClaims = permissions.ToClaimValues();
        if (permissionClaims.Count > 0)
        {
            claims[AccessClaims.Permission] = permissionClaims;
        }

        if (mustChangePassword)
        {
            claims[AccessClaims.PasswordChangeRequired] = "true";
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            Claims = claims,
            SigningCredentials = new SigningCredentials(CreateSigningKey(settings), SecurityAlgorithms.HmacSha256),
        });

        return new IssuedAccessToken(token, expiresAt);
    }

    /// <summary>How every request's token is checked. Used by the JWT bearer handler.</summary>
    public static TokenValidationParameters CreateValidationParameters(AccessTokenOptions settings) => new()
    {
        ValidIssuer = settings.Issuer,
        ValidAudience = settings.Audience,
        IssuerSigningKey = CreateSigningKey(settings),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = settings.ClockSkew,
        NameClaimType = AccessClaims.UserId,
        RoleClaimType = AccessClaims.Role,
    };

    private static SymmetricSecurityKey CreateSigningKey(AccessTokenOptions settings) => new(settings.SigningKeyBytes);
}

internal sealed record IssuedAccessToken(string Token, DateTime ExpiresAt);
