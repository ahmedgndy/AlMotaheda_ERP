using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Security;

/// <summary>
/// The one place that decides which projects a caller may see and what they may do on one. Every slice uses it,
/// so the rules can't drift apart. Other modules will reach it through a contract when they need it.
/// </summary>
internal sealed class ProjectAccess(ProjectsDbContext db)
{
    /// <summary>
    /// The projects the caller may see, as a query, so the filter runs in SQL: every project for company-wide
    /// roles, otherwise projects with an active membership of the caller.
    /// </summary>
    public IQueryable<Project> VisibleProjects(ProjectCaller caller) =>
        caller.SeesAllProjects
            ? db.Projects
            : db.Projects.Where(project => project.Members.Any(member => member.UserId == caller.UserId && member.EndDate == null));

    /// <summary>The caller's active responsibility on the project, or <c>null</c> when they aren't on it.</summary>
    public Task<MemberResponsibility?> ResponsibilityAsync(int userId, int projectId, CancellationToken cancellationToken) =>
        db.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.UserId == userId && member.EndDate == null)
            .Select(member => (MemberResponsibility?)member.Responsibility)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// The rules for changing a project. Decisions D3, D4, D8 and D14 of the Project Domain Analysis.
/// </summary>
internal static class ProjectRules
{
    /// <summary>
    /// Editing project information needs projects Edit, and, for anyone but an Executive, being this project's
    /// active manager. A completed project can only be changed by an Executive.
    /// </summary>
    public static EditDecision Edit(ProjectCaller caller, MemberResponsibility? responsibility, ProjectStatus status)
    {
        if (!caller.Can(PermissionAction.Edit))
        {
            return EditDecision.NotAllowed;
        }

        if (caller.IsExecutive)
        {
            return EditDecision.Allowed;
        }

        if (responsibility != MemberResponsibility.ProjectManager)
        {
            return EditDecision.NotAllowed;
        }

        return status == ProjectStatus.Completed ? EditDecision.ProjectCompleted : EditDecision.Allowed;
    }

    public static bool CanEdit(ProjectCaller caller, MemberResponsibility? responsibility, ProjectStatus status) =>
        Edit(caller, responsibility, status) == EditDecision.Allowed;

    /// <summary>The code appears on documents, so only an Executive changes it.</summary>
    public static bool CanChangeCode(ProjectCaller caller) => caller.IsExecutive;

    /// <summary>
    /// Membership decides who may open a project, so changing it is a change of access: Executive only, the same
    /// rule as for permissions. The token check is followed by a database check (<see cref="ExecutiveGate"/>).
    /// </summary>
    public static bool CanManageMembers(ProjectCaller caller) => caller.IsExecutive;
}

/// <summary>The answer to "may this caller edit this project?", with the reason when not.</summary>
internal enum EditDecision
{
    Allowed,

    /// <summary>No projects Edit permission, or not an Executive and not this project's active manager (403).</summary>
    NotAllowed,

    /// <summary>The project is completed and the caller isn't an Executive (409 <c>project_completed</c>).</summary>
    ProjectCompleted,
}
