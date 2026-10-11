using System.Buffers.Text;
using System.Security.Cryptography;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Access.Security;

/// <summary>
/// Starts and continues sign-ins: adds refresh-token rows and builds the session response
/// (access token, its expiry and the user's permissions).
/// </summary>
/// <remarks>
/// Methods that add rows never save. The calling slice saves once, so its own changes and the new token
/// are stored together or not at all.
/// </remarks>
internal sealed class SessionFactory(
    AccessDbContext db,
    AccessTokenIssuer issuer,
    PermissionResolver permissions,
    IOptions<AccessSessionOptions> sessionOptions,
    TimeProvider time)
{
    /// <summary>Adds the first refresh token of a new sign-in (a new family).</summary>
    public NewRefreshToken AddNewSignIn(int userId, bool persistent)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var settings = sessionOptions.Value;
        var expiresAt = now.Add(persistent ? settings.PersistentDuration : settings.Duration);

        return Add(userId, Guid.NewGuid(), persistent, expiresAt, now);
    }

    /// <summary>Adds the token that replaces <paramref name="previous"/>: same sign-in, same end time.</summary>
    public NewRefreshToken AddRotation(RefreshToken previous) =>
        Add(previous.UserId, previous.FamilyId, previous.IsPersistent, previous.ExpiresAt, time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Builds the session response for the sign-in that <paramref name="refreshToken"/> belongs to. Call it before
    /// committing, so a failure here can't leave the browser holding an already-swapped cookie.
    /// </summary>
    public async Task<SessionResponse> CreateResponseAsync(SessionUser user, NewRefreshToken refreshToken, CancellationToken cancellationToken)
    {
        var effective = await permissions.ResolveAsync(user.Id, user.RoleId, user.RoleCode, cancellationToken);
        var accessToken = issuer.Issue(user.Id, user.RoleCode, user.MustChangePassword, refreshToken.FamilyId, effective);

        return new SessionResponse(
            accessToken.Token,
            accessToken.ExpiresAt,
            user.MustChangePassword,
            new SessionResponse.SignedInUser(user.Id, user.NameAr, user.NameEn, user.RoleCode, effective.ToResponse()));
    }

    private NewRefreshToken Add(int userId, Guid familyId, bool persistent, DateTime expiresAt, DateTime now)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(RefreshTokenValues.ByteLength);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = SHA256.HashData(tokenBytes),
            FamilyId = familyId,
            IsPersistent = persistent,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });

        return new NewRefreshToken(Base64Url.EncodeToString(tokenBytes), familyId, expiresAt, persistent);
    }
}

/// <summary>Ends sign-ins by revoking refresh tokens in one statement. Runs inside the caller's transaction, if any.</summary>
internal static class SessionRevocation
{
    public static Task<int> RevokeAllForUserAsync(
        AccessDbContext db, int userId, RefreshTokenRevocation reason, DateTime now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.RevokedReason, reason),
                cancellationToken);

    public static Task<int> RevokeFamilyAsync(
        AccessDbContext db, Guid familyId, RefreshTokenRevocation reason, DateTime now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.RevokedReason, reason),
                cancellationToken);
}

internal static class RefreshTokenValues
{
    /// <summary>256 random bits.</summary>
    public const int ByteLength = 32;

    /// <summary>The SHA-256 hash stored for a token, or <c>null</c> if the value can't be one of ours.</summary>
    public static byte[]? TryHash(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64)
        {
            return null;
        }

        // A cookie value is client input: anything that isn't exactly 32 bytes of base64url is not ours.
        if (!Base64Url.IsValid(token, out var decodedLength) || decodedLength != ByteLength)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[ByteLength];
        Base64Url.DecodeFromChars(token, bytes);
        return SHA256.HashData(bytes);
    }
}

/// <summary>
/// The refresh-token cookie: HttpOnly (scripts can't read it), Secure, SameSite=Strict, and sent only to
/// <c>/api/auth</c>. The <c>__Secure-</c> prefix makes browsers refuse it over plain HTTP.
/// </summary>
internal static class RefreshCookie
{
    public const string Name = "__Secure-erp-refresh";
    public const string Path = "/api/auth";

    public static void Write(HttpResponse response, NewRefreshToken token) =>
        response.Cookies.Append(Name, token.Token, CreateOptions(token.IsPersistent ? token.ExpiresAt : null));

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static void Delete(HttpResponse response) => response.Cookies.Delete(Name, CreateOptions(expires: null));

    private static CookieOptions CreateOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        IsEssential = true,

        // Without "Remember me" the cookie ends with the browser session; the sign-in itself still
        // ends after 12 hours because the server checks ExpiresAt.
        Expires = expires is null ? null : new DateTimeOffset(expires.Value, TimeSpan.Zero),
    };
}

/// <param name="FamilyId">The sign-in this token belongs to; access tokens carry it as their "sid".</param>
internal sealed record NewRefreshToken(string Token, Guid FamilyId, DateTime ExpiresAt, bool IsPersistent);

/// <summary>What the session response needs to know about the user.</summary>
internal sealed record SessionUser(int Id, int RoleId, string RoleCode, bool MustChangePassword, string NameAr, string NameEn);

/// <summary>Returned by sign-in, refresh and password change.</summary>
internal sealed record SessionResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    bool MustChangePassword,
    SessionResponse.SignedInUser User)
{
    internal sealed record SignedInUser(int Id, string NameAr, string NameEn, string Role, Dictionary<string, string[]> Permissions);
}
