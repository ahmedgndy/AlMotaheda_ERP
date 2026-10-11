namespace AlMotaheda.Modules.Access.Contracts;

/// <summary>
/// Reads basic facts about user accounts for other modules: who someone is and whether they are active.
/// Always read from the database, never from a cache, so a disabled user shows as disabled at once.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// The users with these ids, in one query. Ids that don't exist are left out of the result.
    /// Users are never deleted, so a missing id was never a user.
    /// </summary>
    Task<IReadOnlyDictionary<int, UserSummary>> FindAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken);

    /// <summary>Active users whose role is one of <paramref name="roleCodes"/>, ordered by English name.</summary>
    Task<IReadOnlyList<UserSummary>> ListActiveAsync(IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken);
}

/// <summary>A user account as other modules see it. Never includes a password hash or sign-in details.</summary>
/// <param name="RoleCode">One of <see cref="AccessRoles"/>.</param>
/// <param name="IsActive">False when the account is disabled.</param>
public sealed record UserSummary(
    int Id,
    string NameAr,
    string NameEn,
    string? JobTitleAr,
    string? JobTitleEn,
    string Email,
    string? Phone,
    string RoleCode,
    bool IsActive);
