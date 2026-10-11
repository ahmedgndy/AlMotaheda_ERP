using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Integration;

/// <summary>The Access module's answer to <see cref="IUserDirectory"/>, for other modules.</summary>
internal sealed class UserDirectory(AccessDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<int, UserSummary>> FindAsync(
        IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<int, UserSummary>();
        }

        var ids = userIds.Distinct().ToArray();

        var users = await db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new UserSummary(
                user.Id,
                user.NameAr,
                user.NameEn,
                user.JobTitleAr,
                user.JobTitleEn,
                user.Email,
                user.Phone,
                user.Role.Code,
                user.Status == UserStatus.Active))
            .ToListAsync(cancellationToken);

        return users.ToDictionary(user => user.Id);
    }

    public async Task<IReadOnlyList<UserSummary>> ListActiveAsync(
        IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        if (roleCodes.Count == 0)
        {
            return [];
        }

        var roles = roleCodes.Distinct(StringComparer.Ordinal).ToArray();

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Status == UserStatus.Active && roles.Contains(user.Role.Code))
            .OrderBy(user => user.NameEn)
            .ThenBy(user => user.Id)
            .Select(user => new UserSummary(
                user.Id,
                user.NameAr,
                user.NameEn,
                user.JobTitleAr,
                user.JobTitleEn,
                user.Email,
                user.Phone,
                user.Role.Code,
                true))
            .ToListAsync(cancellationToken);
    }
}
