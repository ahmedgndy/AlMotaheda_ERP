using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Projects;

/// <summary>A person shown next to a project: the user account's id and names, and whether it is active.</summary>
internal sealed record PersonRef(int UserId, string NameAr, string NameEn, bool IsActive)
{
    public static PersonRef From(int userId, IReadOnlyDictionary<int, UserSummary> users) =>
        users.TryGetValue(userId, out var user)
            ? new PersonRef(user.Id, user.NameAr, user.NameEn, user.IsActive)
            // Not expected: memberships point to access.User with a foreign key and users are never deleted.
            : new PersonRef(userId, $"#{userId}", $"#{userId}", false);
}

/// <summary>What the caller may do on this project, so the frontend shows only the buttons that will work.</summary>
internal sealed record ProjectAbilities(bool Edit, bool ChangeCode, bool ManageMembers);

/// <summary>One project, as returned by <c>GET /api/projects/{id}</c>, create and update.</summary>
internal sealed record ProjectDetails(
    int Id,
    string Code,
    string NameAr,
    string NameEn,
    string? ClientNameAr,
    string ClientNameEn,
    string? LocationAr,
    string? LocationEn,
    string? ContractNo,
    string? AddressAr,
    string? AddressEn,
    decimal? SiteLatitude,
    decimal? SiteLongitude,
    int SiteRadiusMeters,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    string? Notes,
    PersonRef? Manager,
    int MemberCount,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion,
    string? MyResponsibility,
    ProjectAbilities Can)
{
    /// <summary>
    /// Loads a project the caller may see, or <c>null</c> when it doesn't exist or is outside the caller's scope.
    /// </summary>
    public static async Task<ProjectDetails?> LoadAsync(
        ProjectsDbContext db,
        ProjectAccess access,
        IUserDirectory users,
        ProjectCaller caller,
        int projectId,
        CancellationToken cancellationToken)
    {
        var row = await access.VisibleProjects(caller)
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => new
            {
                Project = project,
                ManagerUserId = project.Members
                    .Where(member => member.EndDate == null && member.Responsibility == MemberResponsibility.ProjectManager)
                    .Select(member => (int?)member.UserId)
                    .FirstOrDefault(),
                MemberCount = project.Members.Count(member => member.EndDate == null),
                MyResponsibility = project.Members
                    .Where(member => member.EndDate == null && member.UserId == caller.UserId)
                    .Select(member => (MemberResponsibility?)member.Responsibility)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var people = row.ManagerUserId is { } managerId
            ? await users.FindAsync([managerId], cancellationToken)
            : new Dictionary<int, UserSummary>();

        var project = row.Project;
        var canEdit = ProjectRules.CanEdit(caller, row.MyResponsibility, project.Status);

        return new ProjectDetails(
            project.Id,
            project.Code,
            project.NameAr,
            project.NameEn,
            project.ClientNameAr,
            project.ClientNameEn,
            project.LocationAr,
            project.LocationEn,
            project.ContractNo,
            project.AddressAr,
            project.AddressEn,
            project.SiteLatitude,
            project.SiteLongitude,
            project.SiteRadiusMeters,
            project.StartDate,
            project.EndDate,
            project.Status.ToString(),
            project.Notes,
            row.ManagerUserId is { } id ? PersonRef.From(id, people) : null,
            row.MemberCount,
            project.CreatedAt,
            project.CreatedBy,
            project.UpdatedAt,
            project.UpdatedBy,
            RowVersions.Format(project.RowVersion),
            row.MyResponsibility?.ToString(),
            new ProjectAbilities(
                canEdit,
                canEdit && ProjectRules.CanChangeCode(caller),
                ProjectRules.CanManageMembers(caller)));
    }
}
