using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Security;

/// <summary>
/// The first step of every project workspace endpoint: find the project as the caller may see it. One query,
/// with the same scope rule as the project list (<see cref="ProjectAccess.VisibleProjects"/>).
/// </summary>
internal sealed class WorkspaceAccess(ProjectAccess access)
{
    /// <summary>
    /// The project and what the caller is on it, or <c>null</c> when it doesn't exist or is outside the caller's
    /// scope. Both get the same 404.
    /// </summary>
    public async Task<WorkspaceProject?> FindAsync(ProjectCaller caller, int projectId, CancellationToken cancellationToken)
    {
        var row = await access.VisibleProjects(caller)
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => new
            {
                project.Id,
                project.Code,
                project.NameAr,
                project.NameEn,
                project.Status,
                Responsibility = project.Members
                    .Where(member => member.EndDate == null && member.UserId == caller.UserId)
                    .Select(member => (MemberResponsibility?)member.Responsibility)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new WorkspaceProject(row.Id, row.Code, row.NameAr, row.NameEn, row.Status, row.Responsibility, caller);
    }
}

/// <summary>A project the caller may see, with the answers the workspace rules need.</summary>
internal sealed record WorkspaceProject(
    int Id,
    string Code,
    string NameAr,
    string NameEn,
    ProjectStatus Status,
    MemberResponsibility? Responsibility,
    ProjectCaller Caller)
{
    public bool IsCompleted => Status == ProjectStatus.Completed;

    /// <summary>A completed project is read-only for everyone except an Executive (decision D8).</summary>
    public bool WritesBlocked => IsCompleted && !Caller.IsExecutive;

    public bool CallerIsManager => Responsibility == MemberResponsibility.ProjectManager;

    /// <summary>
    /// True when the caller may change the project's structure (its items, its budget draft): projects Edit, and an
    /// Executive or this project's manager. The same rule as editing the project itself (decision D3).
    /// </summary>
    public bool CallerManagesProject => Caller.Can(PermissionAction.Edit) && (Caller.IsExecutive || CallerIsManager);
}
