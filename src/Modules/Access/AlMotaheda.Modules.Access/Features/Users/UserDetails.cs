using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.Users;

/// <summary>
/// One user as the API returns it: the user page, and the answer to every user change.
/// Never contains the password hash or anything about tokens.
/// </summary>
internal sealed record UserDetails(
    int Id,
    string Email,
    string? Phone,
    string NameAr,
    string NameEn,
    string? JobTitleAr,
    string? JobTitleEn,
    string Role,
    string Status,
    bool MustChangePassword,
    bool IsLockedOut,
    DateTime? LockoutEndsAt,
    bool HasPermissionOverrides,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string RowVersion)
{
    /// <summary>Reads one user in a single query, or <c>null</c> if there is no such user.</summary>
    public static async Task<UserDetails?> LoadAsync(AccessDbContext db, int id, DateTime now, CancellationToken cancellationToken)
    {
        var row = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == id)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.Phone,
                user.NameAr,
                user.NameEn,
                user.JobTitleAr,
                user.JobTitleEn,
                RoleCode = user.Role.Code,
                user.Status,
                user.MustChangePassword,
                user.SignInState.LockoutEndsAt,
                HasOverrides = user.PermissionOverrides.Any(),
                user.SignInState.LastLoginAt,
                user.CreatedAt,
                user.UpdatedAt,
                user.RowVersion,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new UserDetails(
                row.Id,
                row.Email,
                row.Phone,
                row.NameAr,
                row.NameEn,
                row.JobTitleAr,
                row.JobTitleEn,
                row.RoleCode,
                row.Status.ToString(),
                row.MustChangePassword,
                row.LockoutEndsAt > now,
                row.LockoutEndsAt > now ? row.LockoutEndsAt : null,
                row.HasOverrides,
                row.LastLoginAt,
                row.CreatedAt,
                row.UpdatedAt,
                RowVersions.Format(row.RowVersion));
    }
}
