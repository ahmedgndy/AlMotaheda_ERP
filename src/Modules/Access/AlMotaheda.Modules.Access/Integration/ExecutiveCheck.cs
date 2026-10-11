using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Integration;

/// <summary>
/// The Access module's answer to <see cref="IExecutiveCheck"/>: the same caller rules as
/// <see cref="Security.UserManagementGate"/>, read from the database at the moment of the check.
/// </summary>
/// <remarks>
/// It takes no lock. Another module calls it just before its own change, in its own transaction, so a demotion
/// that lands in the milliseconds between the two is not seen. User management itself keeps the stricter gate.
/// </remarks>
internal sealed class ExecutiveCheck(AccessDbContext db, TimeProvider time) : IExecutiveCheck
{
    public async Task<bool> IsActiveExecutiveAsync(int userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        var isActiveExecutive = await db.Users.AsNoTracking().AnyAsync(
            user => user.Id == userId
                    && user.Status == UserStatus.Active
                    && user.Role.Code == RoleCodes.Executive
                    && !user.MustChangePassword,
            cancellationToken);

        return isActiveExecutive
               && await db.RefreshTokens.AsNoTracking().AnyAsync(
                   token => token.FamilyId == sessionId
                            && token.UserId == userId
                            && token.RevokedAt == null
                            && token.ExpiresAt > now,
                   cancellationToken);
    }
}
