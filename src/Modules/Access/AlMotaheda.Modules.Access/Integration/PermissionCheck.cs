using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Integration;

/// <summary>The Access module's answer to <see cref="IPermissionCheck"/>: the same rules as a fresh sign-in.</summary>
internal sealed class PermissionCheck(AccessDbContext db, PermissionResolver resolver, TimeProvider time) : IPermissionCheck
{
    public async Task<bool> HoldsAsync(int userId, Guid sessionId, string module, string action, CancellationToken cancellationToken)
    {
        if (!PermissionActions.TryParse(action, out var wanted))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown permission action.");
        }

        var now = time.GetUtcNow().UtcDateTime;

        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId && candidate.Status == UserStatus.Active && !candidate.MustChangePassword)
            .Select(candidate => new { candidate.RoleId, RoleCode = candidate.Role.Code })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return false;
        }

        var signedIn = await db.RefreshTokens.AsNoTracking().AnyAsync(
            token => token.FamilyId == sessionId && token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now,
            cancellationToken);

        return signedIn && (await resolver.ResolveAsync(userId, user.RoleId, user.RoleCode, cancellationToken)).Has(module, wanted);
    }
}
