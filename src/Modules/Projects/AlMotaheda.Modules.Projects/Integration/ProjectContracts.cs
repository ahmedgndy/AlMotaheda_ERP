using System.Security.Claims;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>The Projects module's answer to <see cref="IProjectAccess"/>, for the other modules.</summary>
internal sealed class ProjectAccessContract(WorkspaceAccess workspace) : IProjectAccess
{
    public async Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(caller) is not { } projectCaller)
        {
            return null;
        }

        var project = await workspace.FindAsync(projectCaller, projectId, cancellationToken);

        return project is null
            ? null
            : new ProjectAccessInfo(
                project.Id,
                project.Code,
                project.NameAr,
                project.NameEn,
                project.IsCompleted,
                project.Responsibility?.ToString(),
                projectCaller.IsExecutive);
    }
}

/// <summary>The Projects module's answer to <see cref="IVisibleProjects"/>, from the same scope rule as the project list.</summary>
internal sealed class VisibleProjectsContract(ProjectAccess access) : IVisibleProjects
{
    public async Task<IReadOnlySet<int>?> IdsAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(caller) is not { } projectCaller)
        {
            return new HashSet<int>();
        }

        if (projectCaller.SeesAllProjects)
        {
            return null;
        }

        return (await access.VisibleProjects(projectCaller).Select(project => project.Id).ToListAsync(cancellationToken)).ToHashSet();
    }
}

/// <summary>The Projects module's answer to <see cref="IProjectItems"/>.</summary>
internal sealed class ProjectItemsContract(ProjectsDbContext db) : IProjectItems
{
    public async Task<IReadOnlyDictionary<int, ProjectItemRef>> FindAsync(
        int projectId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<int, ProjectItemRef>();
        }

        var ids = itemIds.Distinct().ToArray();

        var items = await db.ProjectItems
            .AsNoTracking()
            .Where(item => item.ProjectId == projectId && ids.Contains(item.Id))
            .Select(item => new ProjectItemRef(item.Id, item.ProjectId, item.NameAr, item.NameEn, item.Status == ProjectItemStatus.Active))
            .ToListAsync(cancellationToken);

        return items.ToDictionary(item => item.Id);
    }
}

/// <summary>The Projects module's answer to <see cref="ISupplierDirectory"/>.</summary>
internal sealed class SupplierDirectoryContract(ProjectsDbContext db) : ISupplierDirectory
{
    public async Task<IReadOnlyDictionary<int, SupplierRef>> FindAsync(
        IReadOnlyCollection<int> supplierIds, CancellationToken cancellationToken)
    {
        if (supplierIds.Count == 0)
        {
            return new Dictionary<int, SupplierRef>();
        }

        var ids = supplierIds.Distinct().ToArray();

        var suppliers = await db.Suppliers
            .AsNoTracking()
            .Where(supplier => ids.Contains(supplier.Id))
            .Select(supplier => new SupplierRef(supplier.Id, supplier.NameAr, supplier.NameEn, supplier.Status == SupplierStatus.Active))
            .ToListAsync(cancellationToken);

        return suppliers.ToDictionary(supplier => supplier.Id);
    }
}

/// <summary>The Projects module's answer to <see cref="IProjectDirectory"/>: projects by id, with no caller scope.</summary>
internal sealed class ProjectDirectoryContract(ProjectsDbContext db) : IProjectDirectory
{
    public async Task<IReadOnlyDictionary<int, ProjectRef>> FindAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
        {
            return new Dictionary<int, ProjectRef>();
        }

        var ids = projectIds.Distinct().ToArray();
        return await db.Projects.AsNoTracking()
            .Where(project => ids.Contains(project.Id))
            .Select(project => new ProjectRef(project.Id, project.Code, project.NameAr, project.NameEn, project.Status == ProjectStatus.Completed))
            .ToDictionaryAsync(project => project.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectRef>> ListOpenAsync(CancellationToken cancellationToken) =>
        await db.Projects.AsNoTracking()
            .Where(project => project.Status != ProjectStatus.Completed)
            .OrderBy(project => project.Code)
            .Select(project => new ProjectRef(project.Id, project.Code, project.NameAr, project.NameEn, false))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProjectItemRef>> ListActiveItemsAsync(int projectId, CancellationToken cancellationToken) =>
        await db.ProjectItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Status == ProjectItemStatus.Active)
            .OrderBy(item => item.NameEn)
            .Select(item => new ProjectItemRef(item.Id, item.ProjectId, item.NameAr, item.NameEn, true))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// The Projects module's answer to <see cref="IProjectAudience"/>: the same scope rule as the project list
/// (<see cref="ProjectScopes"/>), read from the database for users who are not the caller.
/// </summary>
internal sealed class ProjectAudienceContract(ProjectsDbContext db, IUserDirectory users) : IProjectAudience
{
    public async Task<IReadOnlySet<int>> WhoSeeAsync(int projectId, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<int>();
        }

        var found = await users.FindAsync(userIds, cancellationToken);
        var companyWide = found.Values.Where(user => user.IsActive && ProjectScopes.IsCompanyWide(user.RoleCode)).Select(user => user.Id);

        var others = found.Values.Where(user => user.IsActive && !ProjectScopes.IsCompanyWide(user.RoleCode)).Select(user => user.Id).ToArray();
        var members = others.Length == 0
            ? []
            : await db.ProjectMembers.AsNoTracking()
                .Where(member => member.ProjectId == projectId && member.EndDate == null && others.Contains(member.UserId))
                .Select(member => member.UserId)
                .ToListAsync(cancellationToken);

        return companyWide.Concat(members).ToHashSet();
    }
}
