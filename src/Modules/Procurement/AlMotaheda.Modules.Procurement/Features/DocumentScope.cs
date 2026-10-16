using System.Security.Claims;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Procurement.Features;

/// <summary>
/// Every procurement document belongs to one project: a caller sees it only when they may see the project
/// (Executive, Accountant and Purchasing see all; managers and engineers their own). Otherwise the answer is the
/// same 404 as a missing document.
/// </summary>
internal static class DocumentScope
{
    public static Task<ProjectAccessInfo?> ProjectAsync(IProjectAccess access, ClaimsPrincipal caller, int? projectId, CancellationToken cancellationToken) =>
        projectId is { } id ? access.FindVisibleAsync(caller, id, cancellationToken) : Task.FromResult<ProjectAccessInfo?>(null);
}
